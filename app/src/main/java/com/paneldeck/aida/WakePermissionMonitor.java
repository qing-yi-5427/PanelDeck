package com.paneldeck.aida;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.provider.Settings;

/** Checks a public Android permission; does not guess private OEM policy state. */
final class WakePermissionMonitor {
    static final String CHANNEL = "wake_permission";
    static final int ID = 9101;
    private static Boolean lastMissing, lastNotifications;
    static boolean notificationsEnabled(Context context) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        NotificationChannel channel = manager.getNotificationChannel(CHANNEL);
        return manager.areNotificationsEnabled() && (channel == null || channel.getImportance() != NotificationManager.IMPORTANCE_NONE);
    }
    static boolean missing(Context context) {
        return DesktopLinkService.enabled(context) && !Settings.canDrawOverlays(context);
    }
    static Intent settings(Context context) {
        return new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                Uri.parse("package:" + context.getPackageName()));
    }
    static synchronized void check(Context context) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        boolean missing = missing(context), notifications = notificationsEnabled(context);
        if (lastMissing != null && lastMissing == missing && lastNotifications == notifications) return;
        lastMissing = missing; lastNotifications = notifications;
        if (!missing) { manager.cancel(ID); return; }
        if (!notifications) return;
        manager.createNotificationChannel(new NotificationChannel(CHANNEL,
                "自动亮屏权限提醒", NotificationManager.IMPORTANCE_HIGH));
        PendingIntent open = PendingIntent.getActivity(context, ID, settings(context),
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        manager.notify(ID, new Notification.Builder(context, CHANNEL)
                .setSmallIcon(R.drawable.ic_launcher)
                .setContentTitle("自动亮屏权限已关闭")
                .setContentText("电脑上线后可能无法打开曜屏，点此恢复权限")
                .setStyle(new Notification.BigTextStyle().bigText("请允许曜屏“显示在其他应用上层”，以便电脑上线时自动打开面板。"))
                .setContentIntent(open).setOnlyAlertOnce(true).setOngoing(true).build());
    }
}
