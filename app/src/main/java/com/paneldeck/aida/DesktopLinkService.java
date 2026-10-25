package com.paneldeck.aida;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.os.BatteryManager;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.os.SystemClock;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.Executors;
import java.util.concurrent.ExecutorService;

/** Normal LAN HTTP/SSE, with no ADB dependency. */
public final class DesktopLinkService extends Service {
    static final String ACTION_UPDATE = "com.paneldeck.aida.DESKTOP_UPDATED";
    static final String KEY_ENABLED = "desktop_link_enabled", KEY_TOKEN = "desktop_pairing_token", KEY_URL = "desktop_lan_url", KEY_KEEPALIVE = "desktop_powered_keepalive";
    private static final String CHANNEL = "desktop_connection";
    private static volatile String latest, mode = "on", status = "等待连接";
    private static volatile long lastSeen, startedAt, localHoldUntil;
    private static volatile boolean requestHold;
    private static volatile DesktopLinkService instance;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private ExecutorService worker;
    private volatile boolean destroyed;
    private volatile HttpURLConnection activeConnection;
    private PowerManager.WakeLock networkWake;
    private String appliedMode = "on";
    private long nextPowerRefresh, lastWakeAttempt;
    private final Semaphore retry = new Semaphore(0);
    private volatile int generation;
    private volatile boolean pairingInvalid;
    private boolean retryTheme;
    private ConnectivityManager connectivity;
    private final ConnectivityManager.NetworkCallback networks = new ConnectivityManager.NetworkCallback() {
        @Override public void onAvailable(Network network) { interruptConnection(); }
        @Override public void onLost(Network network) { interruptConnection(); }
    };
    private synchronized void interruptConnection() {
        generation++;
        retry.release();
        HttpURLConnection connection = activeConnection;
        if (connection != null) new Thread(connection::disconnect, "panel-disconnect").start();
    }

    static boolean enabled(Context c) { return ScheduleManager.prefs(c).getBoolean(KEY_ENABLED, false); }
    static boolean connected() { return lastSeen > 0 && SystemClock.elapsedRealtime() - lastSeen < 12000L; }
    static String json() { return connected() ? latest : null; }
    static String status() { return connected() ? "电脑已连接 · 局域网联动" : status; }
    static String screenMode() {
        return DesktopScreenPolicy.connectionMode(SystemClock.elapsedRealtime(), lastSeen, startedAt, localHoldUntil, mode);
    }
    static void hold() {
        localHoldUntil = SystemClock.elapsedRealtime() + 10 * 60000L; requestHold = true;
        DesktopLinkService s = instance;
        if (s != null) s.interruptConnection();
    }
    static void start(Context c) { c.startForegroundService(new Intent(c, DesktopLinkService.class)); }
    static String saveTheme(SharedPreferences prefs, String selected, String original) {
        synchronized (PanelTheme.class) {
            // Saving unrelated settings must not overwrite a theme changed on the PC.
            if (selected.equals(original)) return PanelTheme.normalize(prefs.getString(PanelTheme.KEY, "classic"));
            String theme = PanelTheme.normalize(selected);
            prefs.edit().putString(PanelTheme.KEY, theme).putString(PanelTheme.PENDING, theme).apply();
            return theme;
        }
    }
    static void reconnect() {
        DesktopLinkService s = instance;
        latest = null; lastSeen = 0; status = "正在重新连接";
        if (s != null) { s.pairingInvalid = false; s.interruptConnection(); }
    }

    @Override public void onCreate() {
        super.onCreate(); instance = this; latest = null; lastSeen = 0; mode = "on"; startedAt = SystemClock.elapsedRealtime();
        NotificationManager manager = getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel(CHANNEL, "电脑连接", NotificationManager.IMPORTANCE_LOW));
        PendingIntent open = PendingIntent.getActivity(this, 9100, new Intent(this, MainActivity.class), PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        startForeground(9100, new Notification.Builder(this, CHANNEL).setSmallIcon(R.drawable.ic_launcher)
                .setContentTitle("曜屏 · 电脑联动").setContentText("通过局域网接收硬件数据与电脑状态").setOngoing(true).setContentIntent(open).build());
        networkWake = ((PowerManager)getSystemService(POWER_SERVICE)).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PanelDeck:lanConnection");
        networkWake.setReferenceCounted(false);
        connectivity = getSystemService(ConnectivityManager.class);
        connectivity.registerDefaultNetworkCallback(networks);
        worker = Executors.newSingleThreadExecutor(); worker.execute(this::runConnection);
        handler.post(watchdog);
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) { return START_STICKY; }
    private final Runnable watchdog = new Runnable() {
        @Override public void run() {
            if (destroyed) return;
            applyMode(false);
            long now = SystemClock.elapsedRealtime();
            if (now >= nextPowerRefresh) {
                nextPowerRefresh = now + 15000L;
                WakePermissionMonitor.check(DesktopLinkService.this);
                sendBroadcast(new Intent(ACTION_UPDATE).setPackage(getPackageName()));
                Intent battery = registerReceiver(null, new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
                boolean plugged = battery != null && battery.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0;
                if (plugged && connected() && ScheduleManager.prefs(DesktopLinkService.this).getBoolean(KEY_KEEPALIVE, false)) {
                    if (networkWake.isHeld()) networkWake.release();
                    networkWake.acquire(20000L); // Optional compatibility mode, only while connected and powered.
                } else if (networkWake.isHeld()) networkWake.release();
            }
            handler.postDelayed(this, 1000L);
        }
    };
    private void applyMode(boolean hasData) {
        String proposed = screenMode();
        boolean changed = !proposed.equals(appliedMode);
        PowerManager power = (PowerManager)getSystemService(POWER_SERVICE);
        long now = SystemClock.elapsedRealtime();
        boolean locked = getSystemService(android.app.KeyguardManager.class).isKeyguardLocked();
        if (DesktopScreenPolicy.needsWake(connected(), proposed, power.isInteractive(), locked, MainActivity.panelResumed)
                && now - lastWakeAttempt >= 15000L) {
            lastWakeAttempt = now; ScreenWake.wake(this);
        }
        if ("off".equals(proposed) && !"off".equals(appliedMode)) {
            ScreenWake.release(); ScreenOffAdmin.lockIfEnabled(this);
        }
        appliedMode = proposed;
        if (changed || hasData) sendBroadcast(new Intent(ACTION_UPDATE).setPackage(getPackageName()));
    }
    private void accept(String raw, int expectedGeneration) throws Exception {
        JSONObject data = new JSONObject(raw), pc = data.getJSONObject("pc");
        if (data.optInt("protocolVersion", 0) != 2) throw new IllegalStateException("电脑端版本不兼容，请更新电脑端");
        String proposed = DesktopScreenPolicy.decide(pc.getBoolean("suspended"), pc.getBoolean("manualOff"));
        data.getJSONObject("screen").put("mode", proposed);
        if (expectedGeneration != generation || destroyed) return;
        synchronized (PanelTheme.class) {
            SharedPreferences prefs = ScheduleManager.prefs(this);
            String theme = data.optString("panelTheme", "");
            if (PanelTheme.isKnown(theme) && !prefs.contains(PanelTheme.PENDING) &&
                    !theme.equals(prefs.getString(PanelTheme.KEY, "classic"))) prefs.edit().putString(PanelTheme.KEY, theme).apply();
        }
        mode = proposed; latest = data.toString(); lastSeen = SystemClock.elapsedRealtime(); status = "电脑已连接";
        handler.post(() -> { if (expectedGeneration == generation && !destroyed) applyMode(true); });
    }
    private String batteryQuery() {
        Intent battery = registerReceiver(null, new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
        int level = battery == null ? 0 : battery.getIntExtra(BatteryManager.EXTRA_LEVEL, 0) * 100 / Math.max(1, battery.getIntExtra(BatteryManager.EXTRA_SCALE, 100));
        boolean plugged = battery != null && battery.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0;
        return "?battery=" + level + "&charging=" + (plugged ? 1 : 0);
    }
    private void runConnection() {
        int failures = 0;
        while (!destroyed) {
            int attempt = generation;
            long opened = SystemClock.elapsedRealtime();
            try {
                SharedPreferences p = ScheduleManager.prefs(this);
                if (!p.getBoolean(KEY_ENABLED, false)) { stopSelf(); return; }
                if (pairingInvalid) { retry.tryAcquire(30, TimeUnit.SECONDS); continue; }
                String base = LanAddress.normalize(p.getString(KEY_URL, ""));
                String token = p.getString(KEY_TOKEN, "");
                if (!token.matches("[a-fA-F0-9]{64}")) { pairingInvalid = true; throw new IllegalStateException("请先在设置中配对电脑"); }
                syncThemeSafely(base, token, attempt);
                if (requestHold) { request(base + "/api/phone/hold", token, null); requestHold = false; }
                HttpURLConnection connection = connection(base + "/api/phone/events" + batteryQuery(), token);
                activeConnection = connection; connection.setReadTimeout(15000);
                try {
                    int code = connection.getResponseCode();
                    if (code == 401) { pairingInvalid = true; throw new IllegalStateException("配对已失效，请重新配对"); }
                    if (code != 200) throw new java.io.IOException("Unavailable");
                    String type = connection.getContentType();
                    if (type == null || !type.startsWith("text/event-stream")) throw new java.io.IOException("Invalid stream");
                    long nextStatus = 0;
                    try (InputStream in = new java.io.BufferedInputStream(connection.getInputStream())) {
                        while (!destroyed && attempt == generation) {
                            String event = EventStream.readEvent(in);
                            accept(event, attempt);
                            long now = SystemClock.elapsedRealtime();
                            if (now - opened > 10000) failures = 0;
                            if (now >= nextStatus) {
                                request(base + "/api/phone/status" + batteryQuery(), token, null);
                                if (retryTheme) syncThemeSafely(base, token, attempt);
                                nextStatus = now + 10000;
                            }
                        }
                    }
                } finally { connection.disconnect(); if (activeConnection == connection) activeConnection = null; }
            } catch (InterruptedException e) { return; }
            catch (Exception e) {
                failures = Math.min(5, failures + 1);
                status = e instanceof IllegalArgumentException || e instanceof IllegalStateException ? e.getMessage() : "连接中断，正在重连";
                android.util.Log.w("PanelDeckLink", "Connection retry: " + e.getClass().getSimpleName());
            }
            if (!destroyed) {
                try {
                    if (attempt == generation) retry.tryAcquire(EventStream.retryDelay(failures), TimeUnit.MILLISECONDS);
                    retry.drainPermits();
                } catch (InterruptedException e) { return; }
            }
        }
    }
    private void syncThemeSafely(String base, String token, int expectedGeneration) throws Exception {
        try { syncTheme(base, token, expectedGeneration); retryTheme = false; }
        catch (Exception error) {
            if (pairingInvalid) throw error;
            // Optional appearance must never prevent hardware or power-state updates.
            retryTheme = true;
            android.util.Log.w("PanelDeckLink", "Theme sync deferred: " + error.getClass().getSimpleName());
        }
    }
    private void syncTheme(String base, String token, int expectedGeneration) throws Exception {
        SharedPreferences prefs = ScheduleManager.prefs(this);
        String pending, selected;
        synchronized (PanelTheme.class) {
            pending = prefs.getString(PanelTheme.PENDING, "");
            selected = PanelTheme.normalize(pending.isEmpty() ? prefs.getString(PanelTheme.KEY, "classic") : pending);
        }
        HttpURLConnection c = connection(base + "/api/phone/theme", token);
        c.setRequestMethod("POST"); c.setDoOutput(true); c.setRequestProperty("Content-Type", "application/json");
        try {
            byte[] body = new JSONObject().put("theme", selected).put("initialize", pending.isEmpty()).toString().getBytes(StandardCharsets.UTF_8);
            try (java.io.OutputStream out = c.getOutputStream()) { out.write(body); }
            int code = c.getResponseCode();
            if (code == 404) return; // Old PC versions retain local-only theme selection.
            if (code == 401) { pairingInvalid = true; throw new IllegalStateException("配对已失效，请重新配对"); }
            if (code != 200) throw new java.io.IOException("Theme sync unavailable");
            try (InputStream in = c.getInputStream()) {
                byte[] bytes = new byte[1025]; int count = 0, n;
                while (count < bytes.length && (n = in.read(bytes, count, bytes.length - count)) != -1) count += n;
                if (count > 1024) throw new java.io.IOException("Invalid theme response");
                String theme = new JSONObject(new String(bytes, 0, count, StandardCharsets.UTF_8)).optString("theme", "");
                if (!PanelTheme.isKnown(theme)) throw new java.io.IOException("Unknown theme");
                synchronized (PanelTheme.class) {
                    if (destroyed || expectedGeneration != generation || !pending.equals(prefs.getString(PanelTheme.PENDING, ""))) return;
                    prefs.edit().putString(PanelTheme.KEY, theme).remove(PanelTheme.PENDING).apply();
                }
            }
        } finally { c.disconnect(); }
    }
    private static HttpURLConnection connection(String url, String token) throws Exception {
        HttpURLConnection c = (HttpURLConnection)new URL(url).openConnection();
        c.setConnectTimeout(3000); c.setReadTimeout(4000); c.setInstanceFollowRedirects(false);
        if (token != null) c.setRequestProperty("Authorization", "Bearer " + token);
        return c;
    }
    static String request(String url, String token, String body) throws Exception {
        HttpURLConnection c = connection(url, token);
        c.setRequestMethod("POST");
        try {
            if (body != null) {
                c.setDoOutput(true); c.setRequestProperty("Content-Type", "application/json");
                try (java.io.OutputStream out = c.getOutputStream()) { out.write(body.getBytes(StandardCharsets.UTF_8)); }
            }
            if (c.getResponseCode() != 200) throw new IllegalStateException("配对失败，请检查电脑地址、配对码与防火墙");
            try (InputStream in = c.getInputStream(); ByteArrayOutputStream out = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[4096]; int n;
                while ((n = in.read(buffer)) != -1) { if (out.size() + n > 128 * 1024) throw new IllegalStateException("数据异常"); out.write(buffer, 0, n); }
                return out.toString("UTF-8");
            }
        } finally { c.disconnect(); }
    }
    @Override public void onDestroy() {
        destroyed = true; instance = null; handler.removeCallbacksAndMessages(null);
        if (activeConnection != null) activeConnection.disconnect();
        if (worker != null) worker.shutdownNow();
        if (connectivity != null) connectivity.unregisterNetworkCallback(networks);
        if (networkWake != null && networkWake.isHeld()) networkWake.release();
        ScreenWake.release(); latest = null; lastSeen = 0; startedAt = 0;
        super.onDestroy();
    }
    @Override public IBinder onBind(Intent intent) { return null; }
    @Override protected void dump(java.io.FileDescriptor fd, java.io.PrintWriter writer, String[] args) {
        writer.println("connected=" + connected() + " mode=" + screenMode() + " status=" + status);
        writer.println("lastPacketAgeMs=" + (lastSeen == 0 ? -1 : SystemClock.elapsedRealtime() - lastSeen) + " generation=" + generation);
        writer.println("poweredKeepaliveHeld=" + (networkWake != null && networkWake.isHeld()));
        writer.println("backgroundLaunchPermission=" + android.provider.Settings.canDrawOverlays(this)
                + " panelResumed=" + MainActivity.panelResumed + " lastWakeAttempt=" + lastWakeAttempt);
    }
}
