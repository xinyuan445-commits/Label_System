package com.xinyuan.labelbridge;

import android.content.BroadcastReceiver;
import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.content.IntentFilter;
import android.database.Cursor;
import android.net.Uri;
import android.os.Build;
import android.util.Log;

import java.lang.reflect.Method;

public final class Ct58cBridgeInitializer extends ContentProvider {
    private static final String TAG = "LabelBridgeInit";
    private static final String ACTION = "android.intent.ACTION_DECODE_DATA";
    private static final String COMPATIBLE_ACTION = "android.intent.action.SCANRESULT";
    private static final String DATA_TAG = "barcode_string";
    private static BroadcastReceiver liveReceiver;

    @Override
    public boolean onCreate() {
        Context context = getContext();
        if (context == null) return false;
        Context appContext = context.getApplicationContext();
        PdaBridgeServer.start(appContext);
        registerLiveReceiver(appContext);
        configureUrovoScanner();
        return true;
    }

    private static synchronized void registerLiveReceiver(Context context) {
        if (liveReceiver != null) return;
        try {
            IntentFilter filter = new IntentFilter();
            filter.addAction(ACTION);
            filter.addAction(COMPATIBLE_ACTION);
            BroadcastReceiver receiver = new Ct58cScanReceiver();
            if (Build.VERSION.SDK_INT >= 33) {
                context.registerReceiver(receiver, filter, Context.RECEIVER_EXPORTED);
            } else {
                context.registerReceiver(receiver, filter);
            }
            liveReceiver = receiver;
            Log.i(TAG, "Live CT58C receiver registered");
        } catch (Throwable error) {
            Log.w(TAG, "Could not register CT58C receiver", error);
        }
    }

    private static void configureUrovoScanner() {
        try {
            Class<?> managerClass = Class.forName("android.device.ScanManager");
            Object manager = managerClass.getConstructor().newInstance();
            managerClass.getMethod("openScanner").invoke(manager);

            Class<?> propertyClass = Class.forName(
                "android.device.scanner.configuration.PropertyID");
            int actionKey = propertyClass.getField("WEDGE_INTENT_ACTION_NAME").getInt(null);
            int dataKey = propertyClass.getField("WEDGE_INTENT_DATA_STRING_TAG").getInt(null);
            Method setParameterString = managerClass.getMethod(
                "setParameterString",
                int[].class,
                String[].class);
            setParameterString.invoke(
                manager,
                new Object[] {
                    new int[] { actionKey, dataKey },
                    new String[] { ACTION, DATA_TAG }
                });
            managerClass.getMethod("switchOutputMode", int.class).invoke(manager, 0);
            Log.i(TAG, "CT58C scanner configured for intent output");
        } catch (ClassNotFoundException ignored) {
            Log.i(TAG, "Urovo ScanManager not found");
        } catch (Throwable error) {
            Log.w(TAG, "Could not configure CT58C scanner", error);
        }
    }

    @Override public String getType(Uri uri) { return null; }
    @Override public Cursor query(Uri uri, String[] projection, String selection,
        String[] selectionArgs, String sortOrder) { return null; }
    @Override public Uri insert(Uri uri, ContentValues values) { return null; }
    @Override public int delete(Uri uri, String selection, String[] selectionArgs) { return 0; }
    @Override public int update(Uri uri, ContentValues values, String selection,
        String[] selectionArgs) { return 0; }
}
