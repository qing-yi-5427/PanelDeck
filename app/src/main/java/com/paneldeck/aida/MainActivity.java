package com.paneldeck.aida;

import android.app.Activity;
import android.app.Dialog;
import android.app.KeyguardManager;
import android.app.TimePickerDialog;
import android.annotation.SuppressLint;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.BatteryManager;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.text.InputType;
import android.view.GestureDetector;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.webkit.CookieManager;
import android.webkit.SslErrorHandler;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.ArrayAdapter;
import android.widget.AdapterView;
import android.widget.Space;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import java.util.Locale;

public final class MainActivity extends Activity {
    private static final int INK = Color.rgb(8, 10, 11);
    private static final int SIGNAL = Color.rgb(200, 255, 51);
    private static final String KEY_CONFIGURED = "initial_setup_complete";
    static final String ACTION_WAKE = "com.paneldeck.aida.WAKE_PANEL";
    static boolean panelResumed;
    private Button backgroundWakePermission;

    private SharedPreferences prefs;
    private WebView panel;
    private ProgressBar progress;
    private TextView settingsButton;
    private TextView wakePermissionWarning;
    private View batteryIndicator;
    private TextView batteryValue;
    private ProgressBar batteryBar;
    private TextView holidaySyncStatus;
    private FrameLayout sleepOverlay;
    private GestureDetector gestures;
    private boolean receiverRegistered;
    private boolean batteryReceiverRegistered;
    private String activeTheme = "classic";
    private Dialog controlDialog;
    private boolean desktopPageReady;
    private boolean themePreview;

    private final android.os.Handler handler = new android.os.Handler(android.os.Looper.getMainLooper());
    private final Runnable restoreSettingsAfterPreview = () -> {
        themePreview = false;
        applyScreenMode();
        if (controlDialog != null && !isFinishing() && !isDestroyed()) controlDialog.show();
    };
    private final Runnable hideSettingsButton = () -> {
        if (settingsButton != null && settingsButton.getVisibility() == View.VISIBLE) {
            settingsButton.animate().alpha(0f).scaleX(.7f).scaleY(.7f).setDuration(180)
                    .withEndAction(() -> settingsButton.setVisibility(View.GONE)).start();
        }
    };
    private final Runnable stateTicker = new Runnable() {
        @Override public void run() {
            applyScreenMode();
            handler.postDelayed(this, 30_000L);
        }
    };
    private final BroadcastReceiver modeReceiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) { applyScreenMode(); updateDesktopPanel(); updateBackgroundWakePermission(); }
    };
    private final BroadcastReceiver batteryReceiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) { updateBattery(intent); }
    };

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        ScheduleManager.ensureDefaults(this);
        HolidaySync.ensureOfflineData(this);
        prefs = ScheduleManager.prefs(this);
        activeTheme = PanelTheme.normalize(prefs.getString(PanelTheme.KEY, "classic"));
        prepareWakeWindow();
        buildInterface();
        enterImmersiveMode();
        if (DesktopLinkService.enabled(this)) panel.loadUrl(PanelTheme.url(activeTheme));
        else if (savedInstanceState != null) panel.restoreState(savedInstanceState);
        else panel.loadUrl(normalizeUrl(prefs.getString(ScheduleManager.KEY_HOME_URL, ScheduleManager.DEFAULT_HOME)));
        if (DesktopLinkService.enabled(this) && prefs.getString(DesktopLinkService.KEY_URL, "").isEmpty()) {
            handler.postDelayed(this::showControlPanel, 300L);
        } else if (Intent.ACTION_APPLICATION_PREFERENCES.equals(getIntent().getAction())) {
            handler.postDelayed(this::showControlPanel, 300L);
        } else if (ACTION_WAKE.equals(getIntent().getAction())) {
            handler.postDelayed(this::finishScheduledWake, 120L);
        } else if (!prefs.getBoolean(KEY_CONFIGURED, false)) {
            prefs.edit().putLong(ScheduleManager.KEY_OVERRIDE_UNTIL, System.currentTimeMillis() + 10 * 60_000L).apply();
            handler.postDelayed(this::showControlPanel, 450L);
        }
        ScheduleManager.scheduleNext(this);
        if (DesktopLinkService.enabled(this)) DesktopLinkService.start(this);
        if (!DesktopLinkService.enabled(this)) HolidaySync.syncAsync(this, false, updated -> {
            if (updated) applyScreenMode();
            if (holidaySyncStatus != null) holidaySyncStatus.setText(HolidaySync.status(this));
        });
    }

    @Override protected void onStart() {
        super.onStart();
        if (!receiverRegistered) {
            IntentFilter filter = new IntentFilter(ScheduleReceiver.ACTION_APPLY);
            filter.addAction(DesktopLinkService.ACTION_UPDATE);
            if (Build.VERSION.SDK_INT >= 33) registerReceiver(modeReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
            else registerLegacyReceiver(filter);
            receiverRegistered = true;
        }
        if (!batteryReceiverRegistered) {
            IntentFilter filter = new IntentFilter(Intent.ACTION_BATTERY_CHANGED);
            Intent sticky;
            if (Build.VERSION.SDK_INT >= 33) {
                sticky = registerReceiver(batteryReceiver, filter, Context.RECEIVER_EXPORTED);
            } else {
                sticky = registerLegacyBatteryReceiver(filter);
            }
            if (sticky != null) updateBattery(sticky);
            batteryReceiverRegistered = true;
        }
    }

    @SuppressLint("UnspecifiedRegisterReceiverFlag")
    private void registerLegacyReceiver(IntentFilter filter) {
        registerReceiver(modeReceiver, filter);
    }

    @SuppressLint("UnspecifiedRegisterReceiverFlag")
    private Intent registerLegacyBatteryReceiver(IntentFilter filter) {
        return registerReceiver(batteryReceiver, filter);
    }

    @Override protected void onResume() {
        super.onResume();
        panelResumed = true;
        updateBackgroundWakePermission();
        enterImmersiveMode();
        panel.onResume();
        panel.resumeTimers();
        if (controlDialog != null && !controlDialog.isShowing() && !themePreview) controlDialog.show();
        updateDesktopPanel();
        handler.removeCallbacks(stateTicker);
        handler.post(stateTicker);
    }

    @Override protected void onPause() {
        panelResumed = false;
        if (themePreview) {
            themePreview = false;
            handler.removeCallbacks(restoreSettingsAfterPreview);
            applyScreenMode();
        }
        handler.removeCallbacks(stateTicker);
        panel.onPause();
        if (DesktopLinkService.enabled(this)) panel.pauseTimers();
        super.onPause();
    }

    @Override protected void onStop() {
        if (receiverRegistered) {
            unregisterReceiver(modeReceiver);
            receiverRegistered = false;
        }
        if (batteryReceiverRegistered) {
            unregisterReceiver(batteryReceiver);
            batteryReceiverRegistered = false;
        }
        super.onStop();
    }

    @Override protected void onDestroy() {
        handler.removeCallbacksAndMessages(null);
        if (controlDialog != null) controlDialog.dismiss();
        super.onDestroy();
    }

    @Override protected void onSaveInstanceState(Bundle outState) {
        panel.saveState(outState);
        super.onSaveInstanceState(outState);
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        applyScreenMode();
        if (Intent.ACTION_APPLICATION_PREFERENCES.equals(intent.getAction())) {
            handler.postDelayed(this::showControlPanel, 180L);
        } else if (ACTION_WAKE.equals(intent.getAction())) {
            prepareWakeWindow();
            handler.postDelayed(this::finishScheduledWake, 80L);
        }
    }

    @Override public void onBackPressed() { showSettingsButton(); }

    @Override public void onConfigurationChanged(android.content.res.Configuration configuration) {
        super.onConfigurationChanged(configuration);
        if (controlDialog != null) sizeControlPanel(controlDialog);
    }

    private void enterImmersiveMode() {
        getWindow().setStatusBarColor(Color.TRANSPARENT);
        getWindow().setNavigationBarColor(Color.TRANSPARENT);
        if (Build.VERSION.SDK_INT >= 30) {
            getWindow().setDecorFitsSystemWindows(false);
            WindowInsetsController controller = getWindow().getDecorView().getWindowInsetsController();
            if (controller != null) {
                controller.hide(WindowInsets.Type.statusBars() | WindowInsets.Type.navigationBars());
                controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            }
        } else {
            getWindow().getDecorView().setSystemUiVisibility(
                    View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                            | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
        }
    }

    private void buildInterface() {
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);
        panel = new WebView(this);
        configurePanel();
        root.addView(panel, match());

        progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progress.setMax(100);
        progress.setProgressTintList(android.content.res.ColorStateList.valueOf(SIGNAL));
        progress.setProgressBackgroundTintList(android.content.res.ColorStateList.valueOf(Color.TRANSPARENT));
        root.addView(progress, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(2), Gravity.TOP));

        batteryIndicator = buildBatteryIndicator();
        FrameLayout.LayoutParams batteryLayout = new FrameLayout.LayoutParams(dp(112), dp(34), Gravity.TOP | Gravity.END);
        batteryLayout.setMargins(0, dp(54), dp(17), 0);
        root.addView(batteryIndicator, batteryLayout);

        settingsButton = new TextView(this);
        settingsButton.setText("⚙");
        settingsButton.setTextColor(INK);
        settingsButton.setTextSize(24);
        settingsButton.setGravity(Gravity.CENTER);
        settingsButton.setContentDescription("面板设置");
        settingsButton.setElevation(dp(12));
        settingsButton.setBackground(circle(SIGNAL));
        settingsButton.setVisibility(View.GONE);
        settingsButton.setOnClickListener(v -> showControlPanel());
        FrameLayout.LayoutParams buttonLayout = new FrameLayout.LayoutParams(dp(54), dp(54), Gravity.END | Gravity.BOTTOM);
        buttonLayout.setMargins(0, 0, dp(20), dp(24));
        root.addView(settingsButton, buttonLayout);

        wakePermissionWarning = text("自动亮屏权限已关闭 · 点击恢复", 13, Color.rgb(94, 55, 8));
        wakePermissionWarning.setGravity(Gravity.CENTER);
        wakePermissionWarning.setPadding(dp(16), dp(12), dp(16), dp(12));
        wakePermissionWarning.setBackground(rounded(Color.rgb(255, 231, 185), 12));
        wakePermissionWarning.setVisibility(View.GONE);
        wakePermissionWarning.setOnClickListener(v -> {
            try {
                if (WakePermissionMonitor.missing(this)) startActivity(WakePermissionMonitor.settings(this));
                else if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)
                        != android.content.pm.PackageManager.PERMISSION_GRANTED
                        && (!prefs.getBoolean("notification_permission_requested", false)
                            || shouldShowRequestPermissionRationale(android.Manifest.permission.POST_NOTIFICATIONS))) {
                    prefs.edit().putBoolean("notification_permission_requested", true).apply();
                    requestPermissions(new String[]{android.Manifest.permission.POST_NOTIFICATIONS}, 9101);
                } else startActivity(new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, getPackageName()));
            }
            catch (Exception error) { startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + getPackageName()))); }
        });
        FrameLayout.LayoutParams warningLayout = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        warningLayout.setMargins(dp(16), 0, dp(80), dp(24));
        root.addView(wakePermissionWarning, warningLayout);

        sleepOverlay = buildSleepOverlay();
        sleepOverlay.setVisibility(View.GONE);
        root.addView(sleepOverlay, match());

        gestures = new GestureDetector(this, new GestureDetector.SimpleOnGestureListener() {
            @Override public boolean onDown(MotionEvent e) { return true; }
            @Override public boolean onFling(MotionEvent first, MotionEvent last, float velocityX, float velocityY) {
                if (first == null || last == null) return false;
                float dx = last.getX() - first.getX();
                float dy = last.getY() - first.getY();
                if (Math.abs(dy) > Math.abs(dx) && Math.abs(dy) > dp(55)) {
                    if (dy < 0 && velocityY < -250) showSettingsButton();
                    else if (dy > 0 && velocityY > 250) hideSettingsButtonNow();
                }
                return false;
            }
        });
        View.OnTouchListener gestureListener = (v, event) -> {
            gestures.onTouchEvent(event);
            return false;
        };
        panel.setOnTouchListener(gestureListener);
        sleepOverlay.setOnTouchListener(gestureListener);
        setContentView(root);
    }

    private View buildBatteryIndicator() {
        LinearLayout indicator = new LinearLayout(this);
        indicator.setOrientation(LinearLayout.VERTICAL);
        indicator.setGravity(Gravity.END);
        indicator.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_YES);

        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL | Gravity.END);

        TextView label = text("PHONE", 9, Color.rgb(154, 158, 162));
        label.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        label.setLetterSpacing(.16f);

        batteryValue = text("--%", 11, Color.WHITE);
        batteryValue.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        batteryValue.setLetterSpacing(.08f);
        batteryValue.setGravity(Gravity.END);

        row.addView(label, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(20)));
        row.addView(new Space(this), new LinearLayout.LayoutParams(dp(9), 1));
        row.addView(batteryValue, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(20)));
        indicator.addView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(21)));

        batteryBar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        batteryBar.setMax(100);
        batteryBar.setProgress(0);
        batteryBar.setProgressBackgroundTintList(android.content.res.ColorStateList.valueOf(Color.rgb(42, 44, 45)));
        LinearLayout.LayoutParams barLayout = new LinearLayout.LayoutParams(dp(88), dp(2));
        barLayout.gravity = Gravity.END;
        barLayout.topMargin = dp(2);
        indicator.addView(batteryBar, barLayout);
        return indicator;
    }

    private void updateBattery(Intent intent) {
        if (batteryValue == null || batteryBar == null || intent == null) return;
        int level = intent.getIntExtra(BatteryManager.EXTRA_LEVEL, -1);
        int scale = intent.getIntExtra(BatteryManager.EXTRA_SCALE, 100);
        int percent = level < 0 || scale <= 0 ? 0 : Math.max(0, Math.min(100, Math.round(level * 100f / scale)));
        int status = intent.getIntExtra(BatteryManager.EXTRA_STATUS, BatteryManager.BATTERY_STATUS_UNKNOWN);
        boolean charging = status == BatteryManager.BATTERY_STATUS_CHARGING
                || status == BatteryManager.BATTERY_STATUS_FULL;
        int color = percent <= 20 ? Color.rgb(255, 175, 72)
                : charging ? Color.rgb(103, 157, 255) : Color.rgb(220, 224, 218);
        batteryValue.setText(charging ? percent + "%  ⚡" : percent + "%");
        batteryValue.setTextColor(color);
        batteryBar.setProgress(percent);
        batteryBar.setProgressTintList(android.content.res.ColorStateList.valueOf(color));
        String state = charging ? "，正在充电" : "";
        batteryIndicator.setContentDescription("手机电量 " + percent + "%" + state);
    }

    private void configurePanel() {
        WebSettings settings = panel.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setDatabaseEnabled(true);
        settings.setBuiltInZoomControls(true);
        settings.setDisplayZoomControls(false);
        settings.setSupportZoom(true);
        settings.setUseWideViewPort(true);
        settings.setLoadWithOverviewMode(true);
        settings.setMediaPlaybackRequiresUserGesture(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_COMPATIBILITY_MODE);
        settings.setAllowFileAccess(false);
        CookieManager.getInstance().setAcceptCookie(true);
        panel.setBackgroundColor(Color.BLACK);
        panel.setOverScrollMode(View.OVER_SCROLL_NEVER);
        panel.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                String scheme = request.getUrl().getScheme();
                return !("http".equalsIgnoreCase(scheme) || "https".equalsIgnoreCase(scheme));
            }
            @Override public void onReceivedSslError(WebView view, SslErrorHandler handler, android.net.http.SslError error) {
                handler.cancel();
                Toast.makeText(MainActivity.this, "面板证书无效，已停止加载", Toast.LENGTH_LONG).show();
            }
            @Override public void onPageFinished(WebView view, String url) {
                if (PanelTheme.isLocalUrl(url)) {
                    desktopPageReady = true;
                    applyPanelTheme(activeTheme);
                    updateDesktopPanel();
                    return;
                }
                // Some sensor pages disable user scaling in their viewport. Re-enable it for panel inspection.
                view.evaluateJavascript("(function(){var m=document.querySelector('meta[name=viewport]');"
                        + "if(!m){m=document.createElement('meta');m.name='viewport';document.head.appendChild(m);}"
                        + "var c=m.content||'width=device-width';"
                        + "c=c.replace(/user-scalable\\s*=\\s*[^,]+/ig,'').replace(/maximum-scale\\s*=\\s*[^,]+/ig,'');"
                        + "m.content=c+',maximum-scale=5,user-scalable=yes';})();", null);
            }
        });
        panel.setWebChromeClient(new WebChromeClient() {
            @Override public void onProgressChanged(WebView view, int value) {
                progress.setProgress(value);
                progress.setVisibility(value >= 100 ? View.GONE : View.VISIBLE);
            }
        });
    }

    private FrameLayout buildSleepOverlay() {
        FrameLayout overlay = new FrameLayout(this);
        overlay.setBackgroundColor(Color.BLACK);
        overlay.setClickable(true);
        overlay.setLongClickable(true);
        TextView hint = new TextView(this);
        hint.setText("长按临时唤醒");
        hint.setTextColor(Color.rgb(14, 14, 14));
        hint.setTextSize(12);
        hint.setGravity(Gravity.CENTER);
        overlay.addView(hint, match());
        overlay.setOnLongClickListener(v -> {
            if (DesktopLinkService.enabled(this)) {
                DesktopLinkService.hold();
                applyScreenMode();
                return true;
            }
            prefs.edit().putLong(ScheduleManager.KEY_OVERRIDE_UNTIL, System.currentTimeMillis() + 10 * 60_000L).apply();
            ScheduleManager.scheduleNext(this);
            applyScreenMode();
            Toast.makeText(this, "临时唤醒 10 分钟", Toast.LENGTH_SHORT).show();
            return true;
        });
        return overlay;
    }

    private void showSettingsButton() {
        handler.removeCallbacks(hideSettingsButton);
        settingsButton.animate().cancel();
        settingsButton.setVisibility(View.VISIBLE);
        settingsButton.setAlpha(0f);
        settingsButton.setScaleX(.72f);
        settingsButton.setScaleY(.72f);
        settingsButton.animate().alpha(1f).scaleX(1f).scaleY(1f).setDuration(180).start();
        handler.postDelayed(hideSettingsButton, 5_000L);
    }

    private void hideSettingsButtonNow() {
        handler.removeCallbacks(hideSettingsButton);
        if (settingsButton.getVisibility() != View.VISIBLE) return;
        settingsButton.animate().cancel();
        settingsButton.animate().alpha(0f).translationY(dp(14)).scaleX(.72f).scaleY(.72f)
                .setDuration(160).withEndAction(() -> {
                    settingsButton.setVisibility(View.GONE);
                    settingsButton.setTranslationY(0f);
                }).start();
    }

    private void showControlPanel() {
        if (controlDialog != null) { controlDialog.show(); return; }
        handler.removeCallbacks(hideSettingsButton);
        settingsButton.setVisibility(View.GONE);
        SharedPreferences p = prefs;

        Dialog dialog = new Dialog(this);
        controlDialog = dialog;
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);

        LinearLayout shell = new LinearLayout(this);
        shell.setOrientation(LinearLayout.VERTICAL);
        shell.setBackground(rounded(Color.rgb(242, 241, 235), 24));

        LinearLayout header = new LinearLayout(this);
        header.setTag("control-header");
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setPadding(dp(22), dp(17), dp(14), dp(17));
        header.setBackground(topRounded(INK, 24));
        LinearLayout heading = new LinearLayout(this);
        heading.setOrientation(LinearLayout.VERTICAL);
        TextView eyebrow = text("曜屏  /  PANEL CONTROL", 10, SIGNAL);
        eyebrow.setTag("control-eyebrow");
        eyebrow.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        eyebrow.setLetterSpacing(.12f);
        TextView title = text("面板设置", 23, Color.WHITE);
        title.setTag("control-title");
        title.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        title.setPadding(0, dp(3), 0, 0);
        heading.addView(eyebrow, matchWrap());
        heading.addView(title, matchWrap());
        header.addView(heading, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        TextView close = text("×", 28, Color.rgb(190, 195, 190));
        close.setGravity(Gravity.CENTER);
        close.setContentDescription("关闭设置");
        close.setBackground(circle(Color.rgb(29, 34, 35)));
        close.setOnClickListener(v -> dialog.dismiss());
        header.addView(close, new LinearLayout.LayoutParams(dp(42), dp(42)));
        shell.addView(header, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        LinearLayout form = new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        form.setPadding(dp(18), dp(16), dp(18), dp(16));

        LinearLayout stateCard = card(INK);
        TextView stateDot = text(ScheduleManager.isOff(this, System.currentTimeMillis()) ? "●  屏幕已息屏" : "●  面板常亮", 12, SIGNAL);
        stateDot.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        TextView state = text(scheduleSummary(), 12, Color.rgb(184, 190, 185));
        state.setPadding(0, dp(4), 0, 0);
        stateCard.addView(stateDot, matchWrap());
        stateCard.addView(state, matchWrap());
        form.addView(stateCard, cardParams());

        form.addView(section("面板外观"), matchWrap());
        final String themeAtOpen = PanelTheme.normalize(prefs.getString(PanelTheme.KEY, "classic"));
        applyPanelTheme(themeAtOpen);
        LinearLayout appearanceCard = card(Color.WHITE);
        // A separate, scrollable choice dialog has room for both the theme name
        // and its description even on narrow screens and in landscape. OEM
        // spinner dropdown templates can impose a fixed, single-line row height.
        Spinner themePicker = new Spinner(this, Spinner.MODE_DIALOG);
        themePicker.setContentDescription("选择面板主题");
        themePicker.setPrompt("选择面板主题");
        themePicker.setMinimumHeight(dp(52));
        ArrayAdapter<String> themeAdapter = new ArrayAdapter<String>(this, android.R.layout.simple_spinner_item, PanelTheme.NAMES) {
            @Override public View getView(int position, View convertView, ViewGroup parent) {
                TextView label = convertView instanceof TextView ? (TextView) convertView : new TextView(MainActivity.this);
                label.setText(PanelTheme.NAMES[position]);
                label.setTextColor(INK);
                label.setTextSize(16);
                label.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
                label.setSingleLine(false);
                label.setEllipsize(null);
                label.setHorizontallyScrolling(false);
                label.setGravity(Gravity.CENTER_VERTICAL);
                label.setMinHeight(dp(52));
                label.setPadding(dp(4), dp(8), dp(8), dp(8));
                label.setLayoutParams(new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
                return label;
            }
            @Override public View getDropDownView(int position, View convertView, ViewGroup parent) {
                android.widget.CheckedTextView label = convertView instanceof android.widget.CheckedTextView
                        ? (android.widget.CheckedTextView) convertView : new android.widget.CheckedTextView(MainActivity.this);
                bindThemeChoice(label, position, position == themePicker.getSelectedItemPosition());
                return label;
            }
        };
        themePicker.setAdapter(themeAdapter);
        themePicker.setSelection(PanelTheme.indexOf(activeTheme));
        TextView themeDescription = text(PanelTheme.DESCRIPTIONS[PanelTheme.indexOf(activeTheme)], 13, Color.rgb(99, 105, 101));
        themeDescription.setLineSpacing(dp(2), 1f);
        themeDescription.setPadding(dp(4), dp(2), dp(4), dp(12));
        themePicker.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (controlDialog != dialog) return;
                applyPanelTheme(PanelTheme.IDS[position]);
                themeDescription.setText(PanelTheme.DESCRIPTIONS[position]);
            }
            @Override public void onNothingSelected(AdapterView<?> parent) { }
        });
        Button previewTheme = smallButton("全屏预览 4 秒");
        styleOutlineButton(previewTheme);
        previewTheme.setMinHeight(dp(48));
        previewTheme.setMinimumHeight(dp(48));
        previewTheme.setPadding(dp(12), dp(10), dp(12), dp(10));
        previewTheme.setOnClickListener(v -> {
            if (!PanelTheme.isLocalUrl(panel.getUrl())) {
                Toast.makeText(this, "保存电脑联动设置后，即可预览内置面板主题", Toast.LENGTH_SHORT).show();
                return;
            }
            applyPanelTheme(PanelTheme.IDS[themePicker.getSelectedItemPosition()]);
            // Preview is local and temporary: it never sends a hold/wake request to the PC.
            themePreview = true;
            applyScreenMode();
            updateDesktopPanel();
            dialog.hide();
            handler.removeCallbacks(restoreSettingsAfterPreview);
            handler.postDelayed(restoreSettingsAfterPreview, 4_000L);
        });
        appearanceCard.addView(themePicker, matchWrap());
        appearanceCard.addView(themeDescription, matchWrap());
        appearanceCard.addView(previewTheme, matchWrap());
        TextView themeHint = text("保存后生效；取消或关闭会恢复原主题。仅用于曜屏内置面板。", 12, Color.rgb(99, 105, 101));
        themeHint.setLineSpacing(dp(2), 1f);
        themeHint.setPadding(dp(4), dp(10), dp(4), dp(2));
        appearanceCard.addView(themeHint, matchWrap());
        form.addView(appearanceCard, cardParams());

        form.addView(section("面板连接"), matchWrap());
        Switch desktop = new Switch(this);
        desktop.setText("曜屏电脑端 · 局域网联动");
        desktop.setTextColor(INK);
        desktop.setChecked(DesktopLinkService.enabled(this));
        form.addView(desktop, matchWrap());
        TextView desktopHint = text("手机和电脑连接同一局域网。在电脑设置中开启配对，输入地址和配对码。电脑醒着就亮屏，睡眠或关机后息屏；亮度跟随手机系统和自动亮度。", 12, Color.rgb(99, 105, 101));
        desktopHint.setPadding(0, dp(8), 0, dp(12));
        form.addView(desktopHint, matchWrap());
        EditText computerAddress = input("电脑地址，例如 http://192.168.1.20:8080", p.getString(DesktopLinkService.KEY_URL, ""));
        EditText pairCode = input("电脑设置中显示的 8 位配对码", "");
        pairCode.setInputType(InputType.TYPE_CLASS_NUMBER);
        Button pairButton = smallButton("配对此电脑");
        TextView pairStatus = text("配对成功后点击底部保存。配对码不用于日常连接。", 12, Color.rgb(99, 105, 101));
        String[] pairedAddress = { p.getString(DesktopLinkService.KEY_URL, "") };
        String[] pairedToken = { p.getString(DesktopLinkService.KEY_TOKEN, "") };
        form.addView(computerAddress, matchWrap()); form.addView(pairCode, matchWrap()); form.addView(pairButton, matchWrap()); form.addView(pairStatus, matchWrap());
        pairButton.setOnClickListener(v -> {
            String endpoint;
            try { endpoint = LanAddress.normalize(computerAddress.getText().toString()); }
            catch (IllegalArgumentException e) { computerAddress.setError(e.getMessage()); return; }
            String code = pairCode.getText().toString().trim();
            if (!code.matches("[0-9]{8}")) { pairCode.setError("请输入 8 位配对码"); return; }
            pairButton.setEnabled(false); pairStatus.setText("正在连接电脑…");
            new Thread(() -> {
                try {
                    String body = new org.json.JSONObject().put("code", code).toString();
                    org.json.JSONObject result = new org.json.JSONObject(DesktopLinkService.request(endpoint + "/api/pair", null, body));
                    String token = result.getString("token");
                    if (!token.matches("[a-fA-F0-9]{64}")) throw new IllegalStateException("电脑返回的配对信息无效");
                    runOnUiThread(() -> {
                        if (isFinishing() || !dialog.isShowing()) return;
                        pairedAddress[0] = endpoint; pairedToken[0] = token;
                        computerAddress.setText(endpoint); pairStatus.setText("配对成功，点击保存即可连接。"); pairButton.setEnabled(true);
                    });
                } catch (Exception e) {
                    runOnUiThread(() -> { if (!isFinishing() && dialog.isShowing()) { pairStatus.setText("配对失败，请检查地址、配对码及电脑防火墙。"); pairButton.setEnabled(true); } });
                }
            }, "panel-pairing").start();
        });
        backgroundWakePermission = smallButton("");
        updateBackgroundWakePermission();
        backgroundWakePermission.setOnClickListener(v -> {
            try {
                startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:" + getPackageName())));
            } catch (android.content.ActivityNotFoundException unavailable) {
                startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + getPackageName())));
            }
        });
        form.addView(backgroundWakePermission, matchWrap());
        form.addView(text("自动回到面板需要系统的“显示在其他应用上层”授权。曜屏不会绘制悬浮窗；只在电脑要求亮屏且手机息屏或锁屏时打开面板。HyperOS 还需允许锁屏显示和后台弹出界面。", 12, Color.rgb(99, 105, 101)), matchWrap());
        Button backgroundSettings = smallButton("后台运行与省电设置");
        backgroundSettings.setOnClickListener(v -> startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + getPackageName()))));
        form.addView(backgroundSettings, matchWrap());
        form.addView(text("HyperOS 请将曜屏的省电策略设为“无限制”。系统的电池优化白名单不能替代此设置，否则息屏后仍可能冻结连接，无法接收电脑唤醒状态。", 12, Color.rgb(99, 105, 101)), matchWrap());
        Switch keepalive = new Switch(this);
        keepalive.setText("充电时保持后台连接（兼容模式）"); keepalive.setTextColor(INK);
        keepalive.setChecked(p.getBoolean(DesktopLinkService.KEY_KEEPALIVE, false));
        form.addView(keepalive, matchWrap());
        form.addView(text("默认节能运行。若手机实际息屏后不能及时恢复，可开启此项；只在充电且连接电脑时保持 CPU 唤醒，会增加耗电和发热。", 12, Color.rgb(99, 105, 101)), matchWrap());
        Switch immediateLock = new Switch(this);
        immediateLock.setText("息屏时立即锁屏（可选）"); immediateLock.setTextColor(INK);
        immediateLock.setChecked(p.getBoolean(ScreenOffAdmin.KEY_IMMEDIATE, false) && ScreenOffAdmin.available(this));
        immediateLock.setOnCheckedChangeListener((button, checked) -> {
            if (checked && !ScreenOffAdmin.available(this)) {
                immediateLock.setChecked(false);
                Intent grant = new Intent(android.app.admin.DevicePolicyManager.ACTION_ADD_DEVICE_ADMIN)
                        .putExtra(android.app.admin.DevicePolicyManager.EXTRA_DEVICE_ADMIN, new android.content.ComponentName(this, ScreenOffAdmin.class))
                        .putExtra(android.app.admin.DevicePolicyManager.EXTRA_ADD_EXPLANATION, "只用于在电脑睡眠或关机后立即关闭并锁定手机屏幕。授权后返回并开启此开关。");
                startActivity(grant);
            }
        });
        form.addView(immediateLock, matchWrap());
        form.addView(text("未启用时沿用黑色遮罩与系统超时。立即锁屏需要系统授权，可能要求输入密码后才能进入其他应用；曜屏仍可在锁屏上展示。", 12, Color.rgb(99, 105, 101)), matchWrap());
        int legacyStart = form.getChildCount();
        EditText home = input("AIDA64 面板地址", p.getString(ScheduleManager.KEY_HOME_URL, ScheduleManager.DEFAULT_HOME));
        form.addView(home, matchWrap());

        LinearLayout powerCard = card(Color.WHITE);
        Switch enabled = new Switch(this);
        enabled.setText("自动控制屏幕常亮");
        enabled.setTextColor(INK);
        enabled.setTextSize(15);
        enabled.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        enabled.setChecked(p.getBoolean(ScheduleManager.KEY_ENABLED, true));
        enabled.setPadding(0, 0, 0, 0);
        int[][] switchStates = new int[][] { new int[] { android.R.attr.state_checked }, new int[] {} };
        enabled.setThumbTintList(new android.content.res.ColorStateList(switchStates, new int[] { SIGNAL, Color.rgb(170, 174, 170) }));
        enabled.setTrackTintList(new android.content.res.ColorStateList(switchStates, new int[] { Color.rgb(88, 105, 45), Color.rgb(215, 216, 211) }));
        powerCard.addView(enabled, matchWrap());
        form.addView(powerCard, cardParams());

        form.addView(section("亮屏时段"), matchWrap());
        LinearLayout scheduleCard = card(Color.WHITE);
        TextView workTitle = text("工作日", 13, INK);
        workTitle.setTypeface(Typeface.DEFAULT_BOLD);
        scheduleCard.addView(workTitle, matchWrap());
        int[] workStart = {p.getInt(ScheduleManager.KEY_WORK_START, 120)};
        int[] workEnd = {p.getInt(ScheduleManager.KEY_WORK_END, 1230)};
        scheduleCard.addView(timeRow("开始", workStart, "结束", workEnd), matchWrap());
        scheduleCard.addView(divider(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(1)));
        TextView restTitle = text("周末 / 节假日", 13, INK);
        restTitle.setTypeface(Typeface.DEFAULT_BOLD);
        restTitle.setPadding(0, dp(12), 0, 0);
        scheduleCard.addView(restTitle, matchWrap());
        int[] restStart = {p.getInt(ScheduleManager.KEY_REST_START, 120)};
        int[] restEnd = {p.getInt(ScheduleManager.KEY_REST_END, 480)};
        scheduleCard.addView(timeRow("开始", restStart, "结束", restEnd), matchWrap());
        form.addView(scheduleCard, cardParams());

        form.addView(section("中国大陆节假日"), matchWrap());
        LinearLayout holidayCard = card(Color.WHITE);
        TextView holidayTitle = text("法定节假日与调休", 14, INK);
        holidayTitle.setTypeface(Typeface.DEFAULT_BOLD);
        TextView holidayStatus = text(HolidaySync.status(this), 11, Color.rgb(99, 105, 101));
        holidaySyncStatus = holidayStatus;
        holidayStatus.setPadding(0, dp(5), 0, 0);
        holidayCard.addView(holidayTitle, matchWrap());
        holidayCard.addView(holidayStatus, matchWrap());
        form.addView(holidayCard, cardParams());
        int legacyEnd = form.getChildCount();
        Runnable updateModeControls = () -> {
            boolean legacy = !desktop.isChecked();
            for (int i = legacyStart; i < legacyEnd; i++) form.getChildAt(i).setVisibility(legacy ? View.VISIBLE : View.GONE);
            computerAddress.setEnabled(!legacy); pairCode.setEnabled(!legacy); pairButton.setEnabled(!legacy);
            immediateLock.setEnabled(!legacy); keepalive.setEnabled(!legacy);
            themePicker.setEnabled(!legacy); previewTheme.setEnabled(!legacy);
        };
        desktop.setOnCheckedChangeListener((button, checked) -> updateModeControls.run());
        updateModeControls.run();

        LinearLayout actions = new LinearLayout(this);
        Button reload = smallButton("重新加载面板");
        styleOutlineButton(reload);
        reload.setOnClickListener(v -> { panel.reload(); Toast.makeText(this, "正在重新加载", Toast.LENGTH_SHORT).show(); });
        Button exact = smallButton(ScheduleManager.canScheduleExactly(this) ? "精确定时已开启" : "开启精确定时");
        styleOutlineButton(exact);
        exact.setEnabled(!ScheduleManager.canScheduleExactly(this));
        exact.setVisibility(desktop.isChecked() ? View.GONE : View.VISIBLE);
        desktop.setOnCheckedChangeListener((button, checked) -> { updateModeControls.run(); exact.setVisibility(checked ? View.GONE : View.VISIBLE); });
        exact.setOnClickListener(v -> requestExactAlarm());
        actions.addView(reload, new LinearLayout.LayoutParams(0, dp(48), 1f));
        actions.addView(new Space(this), new LinearLayout.LayoutParams(dp(8), 1));
        actions.addView(exact, new LinearLayout.LayoutParams(0, dp(48), 1f));
        form.addView(actions, matchWrap());

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setVerticalScrollBarEnabled(false);
        scroll.addView(form);
        shell.addView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));

        LinearLayout footer = new LinearLayout(this);
        footer.setTag("control-footer");
        footer.setGravity(Gravity.END | Gravity.CENTER_VERTICAL);
        footer.setPadding(dp(18), dp(12), dp(18), dp(16));
        footer.setBackground(bottomRounded(Color.rgb(242, 241, 235), 24));
        Button cancel = smallButton("取消");
        cancel.setTextColor(Color.rgb(70, 75, 72));
        cancel.setBackgroundColor(Color.TRANSPARENT);
        cancel.setOnClickListener(v -> dialog.dismiss());
        Button save = smallButton("保存设置");
        save.setTextColor(INK);
        save.setTypeface(Typeface.DEFAULT_BOLD);
        save.setBackground(rounded(SIGNAL, 13));
        footer.addView(cancel, new LinearLayout.LayoutParams(dp(78), dp(48)));
        footer.addView(save, new LinearLayout.LayoutParams(dp(116), dp(48)));
        shell.addView(footer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        save.setOnClickListener(v -> {
            String url = home.getText().toString().trim();
            if (!desktop.isChecked() && url.isEmpty()) { home.setError("请输入 AIDA64 面板地址"); return; }
            if (desktop.isChecked()) {
                String endpoint;
                try { endpoint = LanAddress.normalize(computerAddress.getText().toString()); }
                catch (IllegalArgumentException e) { computerAddress.setError(e.getMessage()); return; }
                if (!endpoint.equals(pairedAddress[0]) || !pairedToken[0].matches("[a-fA-F0-9]{64}")) { pairStatus.setText("请先配对此电脑。"); return; }
            }
            String normalized = normalizeUrl(url);
            activeTheme = DesktopLinkService.saveTheme(p, activeTheme, themeAtOpen);
            p.edit().putBoolean(KEY_CONFIGURED, true)
                    .putBoolean(DesktopLinkService.KEY_ENABLED, desktop.isChecked())
                    .putString(DesktopLinkService.KEY_URL, pairedAddress[0]).putString(DesktopLinkService.KEY_TOKEN, pairedToken[0])
                    .putBoolean(ScreenOffAdmin.KEY_IMMEDIATE, immediateLock.isChecked())
                    .putBoolean(DesktopLinkService.KEY_KEEPALIVE, keepalive.isChecked())
                    .putBoolean(ScheduleManager.KEY_ENABLED, enabled.isChecked())
                    .putString(ScheduleManager.KEY_HOME_URL, normalized)
                    .putInt(ScheduleManager.KEY_WORK_START, workStart[0]).putInt(ScheduleManager.KEY_WORK_END, workEnd[0])
                    .putInt(ScheduleManager.KEY_REST_START, restStart[0]).putInt(ScheduleManager.KEY_REST_END, restEnd[0])
                    .putLong(ScheduleManager.KEY_OVERRIDE_UNTIL, 0L).apply();
            desktopPageReady = false;
            if (desktop.isChecked()) {
                panel.loadUrl(PanelTheme.url(activeTheme));
                DesktopLinkService.reconnect();
                DesktopLinkService.start(this);
            } else {
                stopService(new Intent(this, DesktopLinkService.class));
                panel.resumeTimers();
                panel.loadUrl(normalized);
            }
            ScheduleManager.scheduleNext(this);
            applyScreenMode();
            dialog.dismiss();
        });

        dialog.setContentView(shell);
        dialog.setOnDismissListener(ignored -> {
            holidaySyncStatus = null;
            controlDialog = null;
            themePreview = false;
            handler.removeCallbacks(restoreSettingsAfterPreview);
            applyPanelTheme(prefs.getString(PanelTheme.KEY, "classic"));
            applyScreenMode();
            enterImmersiveMode();
        });
        dialog.show();
        Window window = dialog.getWindow();
        if (window != null) {
            window.setBackgroundDrawableResource(android.R.color.transparent);
            WindowManager.LayoutParams params = window.getAttributes();
            params.dimAmount = .72f;
            window.setAttributes(params);
            window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND);
            // A visible settings dialog stays usable even while the linked PC is asleep.
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
            sizeControlPanel(dialog);
        }
    }

    private void bindThemeChoice(android.widget.CheckedTextView label, int position, boolean checked) {
        String name = PanelTheme.NAMES[position];
        android.text.SpannableString content = new android.text.SpannableString(name + "\n" + PanelTheme.DESCRIPTIONS[position]);
        content.setSpan(new android.text.style.StyleSpan(Typeface.BOLD), 0, name.length(), android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
        content.setSpan(new android.text.style.RelativeSizeSpan(13f / 16f), name.length() + 1, content.length(), android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
        content.setSpan(new android.text.style.ForegroundColorSpan(Color.rgb(99, 105, 101)), name.length() + 1, content.length(), android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
        label.setText(content);
        label.setTextColor(INK);
        label.setTextSize(16);
        label.setSingleLine(false);
        label.setEllipsize(null);
        label.setHorizontallyScrolling(false);
        label.setGravity(Gravity.CENTER_VERTICAL);
        label.setLineSpacing(dp(3), 1f);
        label.setPadding(dp(16), dp(12), dp(16), dp(12));
        label.setMinHeight(dp(80));
        android.content.res.TypedArray choiceStyle = obtainStyledAttributes(new int[] { android.R.attr.listChoiceIndicatorSingle });
        try { label.setCheckMarkDrawable(choiceStyle.getDrawable(0)); }
        finally { choiceStyle.recycle(); }
        label.setCheckMarkTintList(android.content.res.ColorStateList.valueOf(INK));
        label.setChecked(checked);
        label.setBackgroundColor(checked ? Color.rgb(237, 242, 223) : Color.WHITE);
        label.setLayoutParams(new android.widget.AbsListView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
    }

    private void sizeControlPanel(Dialog dialog) {
        Window window = dialog.getWindow();
        if (window == null) return;
        android.util.DisplayMetrics metrics = getResources().getDisplayMetrics();
        boolean shortScreen = metrics.heightPixels / metrics.density < 480;
        View decor = window.getDecorView();
        View header = decor.findViewWithTag("control-header");
        View footer = decor.findViewWithTag("control-footer");
        View eyebrow = decor.findViewWithTag("control-eyebrow");
        TextView title = decor.findViewWithTag("control-title");
        if (header != null) header.setPadding(dp(22), dp(shortScreen ? 8 : 17), dp(14), dp(shortScreen ? 8 : 17));
        if (footer != null) footer.setPadding(dp(18), dp(shortScreen ? 8 : 12), dp(18), dp(shortScreen ? 8 : 16));
        if (eyebrow != null) eyebrow.setVisibility(shortScreen ? View.GONE : View.VISIBLE);
        if (title != null) title.setTextSize(shortScreen ? 20 : 23);
        window.setLayout(Math.min(metrics.widthPixels - dp(28), dp(600)),
                Math.min(metrics.heightPixels - dp(shortScreen ? 20 : 54), dp(760)));
    }

    private void applyScreenMode() {
        if (sleepOverlay == null) return;
        boolean off = !themePreview && ScheduleManager.isOff(this, System.currentTimeMillis());
        boolean desktop = DesktopLinkService.enabled(this);
        if (off) {
            if (Build.VERSION.SDK_INT >= 27) setTurnScreenOn(false);
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            sleepOverlay.setVisibility(View.VISIBLE);
        } else {
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            sleepOverlay.setVisibility(View.GONE);
        }
        if (desktop) {
            batteryIndicator.setVisibility(View.GONE);
            if (off) { panel.evaluateJavascript("window.PanelDeck && PanelDeck.pause(true)", null); panel.pauseTimers(); }
            else { panel.resumeTimers(); panel.evaluateJavascript("window.PanelDeck && PanelDeck.pause(false)", null); }
        } else batteryIndicator.setVisibility(View.VISIBLE);
    }

    private void updateDesktopPanel() {
        if (panel == null || !DesktopLinkService.enabled(this) || !PanelTheme.isLocalUrl(panel.getUrl()) || !desktopPageReady) return;
        if (!themePreview && "off".equals(DesktopLinkService.screenMode())) return;
        if (controlDialog == null && !themePreview) {
            String savedTheme = PanelTheme.normalize(prefs.getString(PanelTheme.KEY, "classic"));
            if (!savedTheme.equals(activeTheme)) applyPanelTheme(savedTheme);
        }
        String json = DesktopLinkService.json();
        if (json == null) panel.evaluateJavascript("window.PanelDeck && PanelDeck.offline()", null);
        else panel.evaluateJavascript("window.PanelDeck && PanelDeck.update(" + json + ")", null);
        Intent battery = registerReceiver(null, new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
        if (battery != null) {
            int level = battery.getIntExtra(BatteryManager.EXTRA_LEVEL, 0) * 100 / Math.max(1, battery.getIntExtra(BatteryManager.EXTRA_SCALE, 100));
            boolean charging = battery.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0;
            panel.evaluateJavascript("window.PanelDeck && PanelDeck.battery(" + level + "," + charging + ")", null);
        }
    }

    private void applyPanelTheme(String theme) {
        activeTheme = PanelTheme.normalize(theme);
        if (panel == null || !desktopPageReady || !PanelTheme.isLocalUrl(panel.getUrl())) return;
        panel.evaluateJavascript("window.PanelDeck && PanelDeck.setTheme('" + activeTheme + "')", null);
    }

    @SuppressWarnings("deprecation")
    private void prepareWakeWindow() {
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED
                | WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON
                | WindowManager.LayoutParams.FLAG_DISMISS_KEYGUARD);
        if (Build.VERSION.SDK_INT >= 27) {
            setShowWhenLocked(true);
            setTurnScreenOn(true);
        }
    }

    private void updateBackgroundWakePermission() {
        if (wakePermissionWarning != null) {
            boolean missing = WakePermissionMonitor.missing(this);
            boolean notifications = WakePermissionMonitor.notificationsEnabled(this);
            wakePermissionWarning.setText(missing ? "自动亮屏权限已关闭 · 点击恢复" : "权限提醒通知已关闭 · 点击开启");
            wakePermissionWarning.setVisibility(DesktopLinkService.enabled(this) && (missing || !notifications) ? View.VISIBLE : View.GONE);
        }
        WakePermissionMonitor.check(this);
        if (backgroundWakePermission != null) backgroundWakePermission.setText(
                Settings.canDrawOverlays(this) ? "自动打开面板：已授权 · 管理权限" : "自动打开面板：需要授权");
    }

    private void finishScheduledWake() {
        prepareWakeWindow();
        applyScreenMode();
        KeyguardManager keyguard = (KeyguardManager) getSystemService(Context.KEYGUARD_SERVICE);
        if (keyguard != null && keyguard.isKeyguardLocked() && !keyguard.isDeviceSecure()) {
            keyguard.requestDismissKeyguard(this, null);
        }
        ScheduleManager.scheduleNext(this);
    }

    private String scheduleSummary() {
        if (DesktopLinkService.enabled(this)) return DesktopLinkService.status();
        long now = System.currentTimeMillis();
        boolean off = ScheduleManager.isOff(this, now);
        long next = ScheduleManager.nextChange(this, now);
        if (next == 0) return prefs.getBoolean(ScheduleManager.KEY_ENABLED, true) ? "计划已启用" : "计划已关闭，屏幕保持常亮";
        return (off ? "当前：息屏" : "当前：常亮") + "  ·  下次切换：" + ScheduleManager.formatNext(this, next);
    }

    private View timeRow(String firstLabel, int[] first, String secondLabel, int[] second) {
        LinearLayout row = new LinearLayout(this);
        Button firstButton = timeButton(firstLabel, first[0]);
        Button secondButton = timeButton(secondLabel, second[0]);
        firstButton.setOnClickListener(v -> pickTime(firstButton, firstLabel, first));
        secondButton.setOnClickListener(v -> pickTime(secondButton, secondLabel, second));
        row.addView(firstButton, new LinearLayout.LayoutParams(0, dp(66), 1f));
        row.addView(new Space(this), new LinearLayout.LayoutParams(dp(8), 1));
        row.addView(secondButton, new LinearLayout.LayoutParams(0, dp(66), 1f));
        return row;
    }

    private Button timeButton(String label, int minute) {
        Button button = smallButton(String.format(Locale.CHINA, "%s\n%s", label, ScheduleManager.formatTime(minute)));
        button.setTextColor(Color.WHITE);
        button.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        button.setBackground(rounded(Color.rgb(26, 30, 31), 12));
        return button;
    }

    private void pickTime(Button button, String label, int[] value) {
        new TimePickerDialog(this, (view, hour, minute) -> {
            value[0] = hour * 60 + minute;
            button.setText(String.format(Locale.CHINA, "%s\n%s", label, ScheduleManager.formatTime(value[0])));
        }, value[0] / 60, value[0] % 60, true).show();
    }

    private void requestExactAlarm() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            try { startActivity(new Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, Uri.parse("package:" + getPackageName()))); }
            catch (Exception e) { startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + getPackageName()))); }
        }
    }

    private String normalizeUrl(String raw) {
        String value = raw == null ? "" : raw.trim();
        if (value.matches("^[a-zA-Z][a-zA-Z0-9+.-]*://.*")) return value;
        return "http://" + value;
    }

    private EditText input(String hint, String value) {
        EditText field = new EditText(this);
        field.setHint(hint);
        field.setHintTextColor(Color.rgb(130, 134, 130));
        field.setText(value);
        field.setTextColor(INK);
        field.setSingleLine(true);
        field.setTextSize(14);
        field.setSelectAllOnFocus(true);
        field.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        field.setPadding(dp(14), 0, dp(14), 0);
        field.setBackground(roundedStroke(Color.WHITE, 12, Color.rgb(204, 207, 200), 1));
        field.setMinHeight(dp(52));
        return field;
    }

    private Button smallButton(String value) {
        Button button = new Button(this);
        button.setText(value);
        button.setTextSize(13);
        button.setAllCaps(false);
        return button;
    }

    private TextView section(String value) {
        TextView label = text(value, 11, Color.rgb(73, 80, 77));
        label.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        label.setLetterSpacing(.08f);
        label.setPadding(dp(2), dp(12), 0, dp(7));
        return label;
    }

    private TextView text(String value, float size, int color) {
        TextView label = new TextView(this);
        label.setText(value);
        label.setTextSize(size);
        label.setTextColor(color);
        return label;
    }

    private GradientDrawable circle(int color) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setShape(GradientDrawable.OVAL);
        drawable.setColor(color);
        return drawable;
    }

    private GradientDrawable rounded(int color, int radiusDp) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(dp(radiusDp));
        return drawable;
    }

    private GradientDrawable roundedStroke(int color, int radiusDp, int strokeColor, int strokeDp) {
        GradientDrawable drawable = rounded(color, radiusDp);
        drawable.setStroke(dp(strokeDp), strokeColor);
        return drawable;
    }

    private GradientDrawable topRounded(int color, int radiusDp) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        float radius = dp(radiusDp);
        drawable.setCornerRadii(new float[] { radius, radius, radius, radius, 0, 0, 0, 0 });
        return drawable;
    }

    private GradientDrawable bottomRounded(int color, int radiusDp) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        float radius = dp(radiusDp);
        drawable.setCornerRadii(new float[] { 0, 0, 0, 0, radius, radius, radius, radius });
        return drawable;
    }

    private LinearLayout card(int color) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(14), dp(13), dp(14), dp(13));
        card.setBackground(rounded(color, 15));
        return card;
    }

    private LinearLayout.LayoutParams cardParams() {
        LinearLayout.LayoutParams params = matchWrap();
        params.setMargins(0, 0, 0, dp(3));
        return params;
    }

    private View divider() {
        View divider = new View(this);
        divider.setBackgroundColor(Color.rgb(229, 230, 225));
        return divider;
    }

    private void styleOutlineButton(Button button) {
        button.setTextColor(INK);
        button.setBackground(roundedStroke(Color.TRANSPARENT, 12, Color.rgb(176, 180, 173), 1));
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private FrameLayout.LayoutParams match() { return new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT); }
    private LinearLayout.LayoutParams matchWrap() { return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT); }
}
