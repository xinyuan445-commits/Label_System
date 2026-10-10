package com.xinyuan.labelbridge;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Bundle;
import android.util.Log;

import java.nio.charset.StandardCharsets;

public final class Ct58cScanReceiver extends BroadcastReceiver {
    private static final String TAG = "LabelScanReceiver";
    private static final long DUPLICATE_WINDOW_MS = 800L;
    private static String lastBarcode = "";
    private static long lastReceivedAt;

    @Override
    public void onReceive(Context context, Intent intent) {
        if (context == null || intent == null) return;
        PdaBridgeServer.start(context.getApplicationContext());

        BarcodePayload payload = readBarcode(intent);
        if (payload == null || payload.value.trim().isEmpty()) {
            Log.w(TAG, "Scan broadcast contained no barcode");
            return;
        }
        if (isDuplicate(payload.value)) return;

        PdaBridgeServer.publish(payload.value, intent.getAction(), payload.field);
    }

    private static synchronized boolean isDuplicate(String barcode) {
        long now = System.currentTimeMillis();
        if (barcode.equals(lastBarcode) && now - lastReceivedAt <= DUPLICATE_WINDOW_MS) {
            Log.i(TAG, "Duplicate scan ignored");
            return true;
        }
        lastBarcode = barcode;
        lastReceivedAt = now;
        return false;
    }

    private static BarcodePayload readBarcode(Intent intent) {
        String[] keys = new String[] {
            "barcode_string",
            "value",
            "com.symbol.datawedge.data_string",
            "data",
            "decode_data",
            "barcode"
        };
        Bundle extras = intent.getExtras();
        if (extras == null) return null;

        BarcodePayload fallback = null;
        for (String key : keys) {
            Object raw = extras.get(key);
            String value = null;
            if (raw instanceof String) {
                value = (String) raw;
            } else if (raw instanceof CharSequence) {
                value = raw.toString();
            } else if (raw instanceof byte[]) {
                value = new String((byte[]) raw, StandardCharsets.UTF_8);
            }
            if (value == null || value.trim().isEmpty()) continue;
            BarcodePayload candidate = new BarcodePayload(value, key);
            if (!value.equals(lastBarcode)) return candidate;
            if (fallback == null) fallback = candidate;
        }
        return fallback;
    }

    private static final class BarcodePayload {
        private final String value;
        private final String field;

        private BarcodePayload(String value, String field) {
            this.value = value;
            this.field = field;
        }
    }
}
