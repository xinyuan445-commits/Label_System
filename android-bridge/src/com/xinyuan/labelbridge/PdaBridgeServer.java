package com.xinyuan.labelbridge;

import android.content.Context;
import android.util.Log;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;

final class PdaBridgeServer {
    static final String PROTOCOL_VERSION = "pda-bridge-v1";
    static final int PORT = 18765;
    private static final String TAG = "LabelPdaBridge";
    private static final long ACTIVE_WINDOW_MS = 3500L;
    private static final int MAX_QUEUE_SIZE = 100;
    private static final Object LOCK = new Object();
    private static final ArrayDeque<ScanEvent> EVENTS = new ArrayDeque<>();
    private static volatile boolean started;
    private static volatile long lastPollAt;

    private PdaBridgeServer() { }

    static void start(Context context) {
        if (started) return;
        synchronized (PdaBridgeServer.class) {
            if (started) return;
            started = true;
        }

        Thread serverThread = new Thread(new Runnable() {
            @Override
            public void run() {
                runServer();
            }
        }, "label-pda-bridge");
        serverThread.setDaemon(true);
        serverThread.start();
        Log.i(TAG, "PDA bridge starting on 127.0.0.1:" + PORT);
    }

    static void publish(String barcode, String action, String field) {
        long now = System.currentTimeMillis();
        if (now - lastPollAt > ACTIVE_WINDOW_MS) {
            Log.i(TAG, "Scan ignored because H5 bridge is inactive");
            return;
        }

        ScanEvent event = new ScanEvent(
            now + "-" + Math.abs(System.nanoTime()),
            barcode,
            action == null ? "" : action,
            field == null ? "" : field,
            now);

        synchronized (LOCK) {
            while (EVENTS.size() >= MAX_QUEUE_SIZE) {
                EVENTS.removeFirst();
            }
            EVENTS.addLast(event);
        }
        Log.i(TAG, "Scan queued for H5; length=" + barcode.length());
    }

    private static void runServer() {
        try {
            ServerSocket server = new ServerSocket();
            server.setReuseAddress(true);
            server.bind(new InetSocketAddress(InetAddress.getByName("127.0.0.1"), PORT));
            while (true) {
                Socket socket = server.accept();
                try {
                    handle(socket);
                } catch (Throwable error) {
                    Log.w(TAG, "Bridge request failed", error);
                } finally {
                    try { socket.close(); } catch (Exception ignored) { }
                }
            }
        } catch (Throwable error) {
            started = false;
            Log.e(TAG, "PDA bridge server stopped", error);
        }
    }

    private static void handle(Socket socket) throws Exception {
        socket.setSoTimeout(3000);
        BufferedReader reader = new BufferedReader(
            new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
        String requestLine = reader.readLine();
        if (requestLine == null || requestLine.isEmpty()) return;

        String header;
        do {
            header = reader.readLine();
        } while (header != null && !header.isEmpty());

        String[] requestParts = requestLine.split(" ");
        if (requestParts.length < 2) {
            respond(socket, 400, "text/plain; charset=utf-8", "Bad request");
            return;
        }

        String method = requestParts[0];
        String requestTarget = requestParts[1];
        if ("OPTIONS".equals(method)) {
            respond(socket, 204, "text/plain", "");
            return;
        }
        if (!"GET".equals(method)) {
            respond(socket, 405, "text/plain; charset=utf-8", "Method not allowed");
            return;
        }

        if (requestTarget.startsWith("/v1/health")) {
            respond(socket, 200, "application/json; charset=utf-8",
                "{\"ready\":true,\"protocol\":\"" + PROTOCOL_VERSION + "\"}");
            return;
        }
        if (requestTarget.startsWith("/v1/events")) {
            lastPollAt = System.currentTimeMillis();
            respond(socket, 200, "application/json; charset=utf-8", drainEvents());
            return;
        }
        if (requestTarget.equals("/") || requestTarget.startsWith("/?")) {
            String target = queryValue(requestTarget, "target");
            if (target == null || !(target.startsWith("http://") || target.startsWith("https://"))) {
                respond(socket, 400, "text/plain; charset=utf-8", "Invalid target");
                return;
            }
            respond(socket, 200, "text/html; charset=utf-8", wrapperHtml(target));
            return;
        }

        respond(socket, 404, "text/plain; charset=utf-8", "Not found");
    }

    private static String queryValue(String requestTarget, String key) {
        int question = requestTarget.indexOf('?');
        if (question < 0 || question >= requestTarget.length() - 1) return null;
        String[] pairs = requestTarget.substring(question + 1).split("&");
        for (String pair : pairs) {
            int equals = pair.indexOf('=');
            if (equals <= 0) continue;
            String name = decode(pair.substring(0, equals));
            if (key.equals(name)) return decode(pair.substring(equals + 1));
        }
        return null;
    }

    private static String decode(String value) {
        try {
            return URLDecoder.decode(value, "UTF-8");
        } catch (Exception error) {
            return "";
        }
    }

    private static String wrapperHtml(String target) {
        return "<!doctype html><html><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no\">"
            + "<style>html,body,iframe{width:100%;height:100%;margin:0;border:0;overflow:hidden;background:#fff}</style>"
            + "</head><body><iframe id=\"app\" allow=\"clipboard-read; clipboard-write\" src=\""
            + escapeHtml(target) + "\"></iframe><script>"
            + "const app=document.getElementById('app');"
            + "async function poll(){try{const r=await fetch('/v1/events',{cache:'no-store'});"
            + "if(r.ok){const items=await r.json();for(const item of items){"
            + "app.contentWindow.postMessage({source:'label-system-pda',type:'scan',payload:item},'*');}}}"
            + "catch(e){}finally{setTimeout(poll,140);}}poll();"
            + "</script></body></html>";
    }

    private static String drainEvents() {
        List<ScanEvent> items = new ArrayList<>();
        synchronized (LOCK) {
            while (!EVENTS.isEmpty()) items.add(EVENTS.removeFirst());
        }
        StringBuilder json = new StringBuilder("[");
        for (int index = 0; index < items.size(); index += 1) {
            if (index > 0) json.append(',');
            json.append(items.get(index).toJson());
        }
        return json.append(']').toString();
    }

    private static void respond(Socket socket, int status, String contentType, String body)
        throws Exception {
        byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
        String statusText = status == 200 ? "OK" : status == 204 ? "No Content" : "Error";
        String headers = "HTTP/1.1 " + status + " " + statusText + "\r\n"
            + "Content-Type: " + contentType + "\r\n"
            + "Content-Length: " + bytes.length + "\r\n"
            + "Cache-Control: no-store\r\n"
            + "Access-Control-Allow-Origin: *\r\n"
            + "Access-Control-Allow-Methods: GET, OPTIONS\r\n"
            + "Connection: close\r\n\r\n";
        OutputStream output = socket.getOutputStream();
        output.write(headers.getBytes(StandardCharsets.UTF_8));
        output.write(bytes);
        output.flush();
    }

    private static String escapeHtml(String value) {
        return value.replace("&", "&amp;")
            .replace("\"", "&quot;")
            .replace("<", "&lt;")
            .replace(">", "&gt;");
    }

    private static String escapeJson(String value) {
        return value.replace("\\", "\\\\")
            .replace("\"", "\\\"")
            .replace("\r", "\\r")
            .replace("\n", "\\n")
            .replace("\t", "\\t");
    }

    private static final class ScanEvent {
        private final String id;
        private final String barcode;
        private final String action;
        private final String field;
        private final long receivedAt;

        private ScanEvent(String id, String barcode, String action, String field, long receivedAt) {
            this.id = id;
            this.barcode = barcode;
            this.action = action;
            this.field = field;
            this.receivedAt = receivedAt;
        }

        private String toJson() {
            return "{\"id\":\"" + escapeJson(id)
                + "\",\"barcode\":\"" + escapeJson(barcode)
                + "\",\"action\":\"" + escapeJson(action)
                + "\",\"field\":\"" + escapeJson(field)
                + "\",\"receivedAt\":" + receivedAt
                + ",\"bridgeVersion\":\"" + PROTOCOL_VERSION + "\"}";
        }
    }
}
