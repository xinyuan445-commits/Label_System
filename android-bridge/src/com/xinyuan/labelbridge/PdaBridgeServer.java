package com.xinyuan.labelbridge;

import android.content.Context;
import android.content.SharedPreferences;
import android.media.AudioManager;
import android.media.ToneGenerator;
import android.os.SystemClock;
import android.util.Log;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URI;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

final class PdaBridgeServer {
    static final String PROTOCOL_VERSION = "pda-bridge-v1";
    static final int PORT = 18765;
    private static final String TAG = "LabelPdaBridge";
    private static final String PREFS = "label_pda_bridge";
    private static final String CONFIG_READY = "config_ready";
    private static final String DEFAULT_DEVELOPMENT_URL = "http://172.16.10.45:5173/";
    private static final String DEFAULT_TEST_URL = "http://172.16.10.45:5174/";
    private static final String DEFAULT_PRODUCTION_URL = "http://172.16.10.45:5175/";
    private static final long ACTIVE_WINDOW_MS = 3500L;
    private static final int MAX_QUEUE_SIZE = 100;
    private static final Object EVENT_LOCK = new Object();
    private static final Object CONFIG_LOCK = new Object();
    private static final ArrayDeque<ScanEvent> EVENTS = new ArrayDeque<>();
    private static volatile Context appContext;
    private static volatile boolean started;
    private static volatile long lastPollAt;

    private PdaBridgeServer() { }

    static void start(Context context) {
        if (context != null) appContext = context.getApplicationContext();
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

    static boolean publish(String barcode, String action, String field) {
        long now = System.currentTimeMillis();
        if (now - lastPollAt > ACTIVE_WINDOW_MS) {
            Log.i(TAG, "Scan ignored because H5 bridge is inactive");
            return false;
        }

        ScanEvent event = new ScanEvent(
            now + "-" + Math.abs(System.nanoTime()),
            barcode,
            action == null ? "" : action,
            field == null ? "" : field,
            now);

        synchronized (EVENT_LOCK) {
            while (EVENTS.size() >= MAX_QUEUE_SIZE) EVENTS.removeFirst();
            EVENTS.addLast(event);
        }
        Log.i(TAG, "Scan queued for H5; length=" + barcode.length());
        playConfiguredTone();
        return true;
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

        Map<String, String> headers = new HashMap<>();
        String header;
        while ((header = reader.readLine()) != null && !header.isEmpty()) {
            int colon = header.indexOf(':');
            if (colon > 0) {
                headers.put(
                    header.substring(0, colon).trim().toLowerCase(Locale.ROOT),
                    header.substring(colon + 1).trim());
            }
        }

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

        if ("GET".equals(method) && requestTarget.startsWith("/v1/health")) {
            respond(socket, 200, "application/json; charset=utf-8",
                "{\"ready\":true,\"protocol\":\"" + PROTOCOL_VERSION + "\"}");
            return;
        }
        if ("GET".equals(method) && requestTarget.startsWith("/v1/events")) {
            lastPollAt = System.currentTimeMillis();
            respond(socket, 200, "application/json; charset=utf-8", drainEvents());
            return;
        }
        if ("GET".equals(method) && requestTarget.startsWith("/v1/config")) {
            respond(socket, 200, "application/json; charset=utf-8",
                getConfig(new HashMap<String, String>()).toJson());
            return;
        }
        if ("POST".equals(method) && requestTarget.startsWith("/v1/config")) {
            String body = readBody(reader, headers.get("content-length"));
            BridgeConfig config = saveConfig(parseParameters(body));
            if (config == null) {
                respond(socket, 400, "application/json; charset=utf-8",
                    "{\"error\":\"Invalid configuration\"}");
            } else {
                respond(socket, 200, "application/json; charset=utf-8", config.toJson());
            }
            return;
        }
        if ("POST".equals(method) && requestTarget.startsWith("/v1/sound")) {
            String body = readBody(reader, headers.get("content-length"));
            String mode = parseParameters(body).get("mode");
            if (!isSoundMode(mode)) {
                respond(socket, 400, "application/json; charset=utf-8",
                    "{\"error\":\"Invalid sound mode\"}");
            } else {
                playTone(mode);
                respond(socket, 200, "application/json; charset=utf-8", "{\"played\":true}");
            }
            return;
        }
        if ("GET".equals(method) && (requestTarget.equals("/") || requestTarget.startsWith("/?"))) {
            BridgeConfig config = getConfig(parseQuery(requestTarget));
            respond(socket, 200, "text/html; charset=utf-8", wrapperHtml(config));
            return;
        }
        if (!"GET".equals(method) && !"POST".equals(method)) {
            respond(socket, 405, "text/plain; charset=utf-8", "Method not allowed");
            return;
        }

        respond(socket, 404, "text/plain; charset=utf-8", "Not found");
    }

    private static String readBody(BufferedReader reader, String contentLengthValue)
        throws Exception {
        int contentLength;
        try {
            contentLength = Integer.parseInt(contentLengthValue == null ? "0" : contentLengthValue);
        } catch (NumberFormatException error) {
            contentLength = 0;
        }
        if (contentLength <= 0 || contentLength > 16384) return "";
        char[] buffer = new char[contentLength];
        int offset = 0;
        while (offset < contentLength) {
            int count = reader.read(buffer, offset, contentLength - offset);
            if (count < 0) break;
            offset += count;
        }
        return new String(buffer, 0, offset);
    }

    private static Map<String, String> parseQuery(String requestTarget) {
        int question = requestTarget.indexOf('?');
        if (question < 0 || question >= requestTarget.length() - 1) return new HashMap<>();
        return parseParameters(requestTarget.substring(question + 1));
    }

    private static Map<String, String> parseParameters(String value) {
        Map<String, String> parameters = new HashMap<>();
        if (value == null || value.isEmpty()) return parameters;
        for (String pair : value.split("&")) {
            int equals = pair.indexOf('=');
            if (equals <= 0) continue;
            parameters.put(decode(pair.substring(0, equals)), decode(pair.substring(equals + 1)));
        }
        return parameters;
    }

    private static String decode(String value) {
        try {
            return URLDecoder.decode(value, "UTF-8");
        } catch (Exception error) {
            return "";
        }
    }

    private static BridgeConfig getConfig(Map<String, String> bootstrap) {
        synchronized (CONFIG_LOCK) {
            SharedPreferences preferences = preferences();
            if (preferences == null) return BridgeConfig.defaults();
            if (!preferences.getBoolean(CONFIG_READY, false)) {
                BridgeConfig initial = BridgeConfig.fromParameters(bootstrap, true);
                persist(preferences, initial);
                return initial;
            }
            return BridgeConfig.fromPreferences(preferences);
        }
    }

    private static BridgeConfig saveConfig(Map<String, String> parameters) {
        BridgeConfig config = BridgeConfig.fromParameters(parameters, false);
        if (config == null) return null;
        synchronized (CONFIG_LOCK) {
            SharedPreferences preferences = preferences();
            if (preferences == null) return null;
            persist(preferences, config);
        }
        Log.i(TAG, "H5 address configuration updated: " + config.environment);
        return config;
    }

    private static SharedPreferences preferences() {
        Context context = appContext;
        return context == null ? null : context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    private static void persist(SharedPreferences preferences, BridgeConfig config) {
        preferences.edit()
            .putBoolean(CONFIG_READY, true)
            .putString("environment", config.environment)
            .putString("developmentUrl", config.developmentUrl)
            .putString("testUrl", config.testUrl)
            .putString("productionUrl", config.productionUrl)
            .putString("scanSound", config.scanSound)
            .apply();
    }

    private static String wrapperHtml(BridgeConfig config) {
        String configJson = config.toJson()
            .replace("<", "\\u003c")
            .replace(">", "\\u003e")
            .replace("&", "\\u0026");
        return "<!doctype html><html><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no\">"
            + "<style>html,body,iframe{width:100%;height:100%;margin:0;border:0;overflow:hidden;background:#fff}</style>"
            + "</head><body><iframe id=\"app\" allow=\"clipboard-read; clipboard-write\" src=\""
            + escapeHtml(config.selectedUrl()) + "\"></iframe><script>"
            + "const app=document.getElementById('app');let config=" + configJson + ";"
            + "function sendConfig(type='config'){app.contentWindow.postMessage({source:'label-system-pda',type,payload:config},'*')}"
            + "app.addEventListener('load',()=>sendConfig());"
            + "window.addEventListener('message',async e=>{if(e.source!==app.contentWindow||!e.data||e.data.source!=='label-system-h5')return;"
            + "let expected='';try{expected=new URL(app.src).origin}catch(x){}if(e.origin!==expected)return;"
            + "if(e.data.type==='config-request'){sendConfig();return;}"
            + "if(e.data.type==='sound-test'){try{const body=new URLSearchParams({mode:String(e.data.payload&&e.data.payload.mode||'')});"
            + "const r=await fetch('/v1/sound',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body});"
            + "if(!r.ok)throw new Error();app.contentWindow.postMessage({source:'label-system-pda',type:'sound-tested',payload:config},'*');}"
            + "catch(x){app.contentWindow.postMessage({source:'label-system-pda',type:'config-error'},'*')}return;}"
            + "if(e.data.type!=='config-save')return;"
            + "try{const p=e.data.payload||{},body=new URLSearchParams();"
            + "for(const k of ['environment','developmentUrl','testUrl','productionUrl','scanSound'])body.set(k,String(p[k]||''));"
            + "const r=await fetch('/v1/config',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body});"
            + "if(!r.ok)throw new Error();config=await r.json();sendConfig('config-saved');"
            + "const next=config[config.environment+'Url'];if(next&&next!==app.src)setTimeout(()=>{app.src=next},80);"
            + "}catch(x){app.contentWindow.postMessage({source:'label-system-pda',type:'config-error'},'*')}});"
            + "async function poll(){try{const r=await fetch('/v1/events',{cache:'no-store'});"
            + "if(r.ok){const items=await r.json();for(const item of items){"
            + "app.contentWindow.postMessage({source:'label-system-pda',type:'scan',payload:item},'*');}}}"
            + "catch(e){}finally{setTimeout(poll,140);}}poll();"
            + "</script></body></html>";
    }

    private static String drainEvents() {
        List<ScanEvent> items = new ArrayList<>();
        synchronized (EVENT_LOCK) {
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
            + "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n"
            + "Access-Control-Allow-Headers: Content-Type\r\n"
            + "Connection: close\r\n\r\n";
        OutputStream output = socket.getOutputStream();
        output.write(headers.getBytes(StandardCharsets.UTF_8));
        output.write(bytes);
        output.flush();
    }

    private static String normalizeUrl(String value) {
        if (value == null) return null;
        String url = value.trim();
        try {
            URI parsed = new URI(url);
            String scheme = parsed.getScheme();
            if (scheme == null || parsed.getHost() == null) return null;
            if (!"http".equalsIgnoreCase(scheme) && !"https".equalsIgnoreCase(scheme)) return null;
            return url;
        } catch (Exception error) {
            return null;
        }
    }

    private static boolean isSoundMode(String value) {
        return "short".equals(value)
            || "soft".equals(value)
            || "double".equals(value)
            || "off".equals(value);
    }

    private static void playConfiguredTone() {
        SharedPreferences preferences = preferences();
        String mode = preferences == null ? "short" : preferences.getString("scanSound", "short");
        playTone(isSoundMode(mode) ? mode : "short");
    }

    private static void playTone(final String mode) {
        if ("off".equals(mode)) return;
        Thread toneThread = new Thread(new Runnable() {
            @Override
            public void run() {
                ToneGenerator tone = null;
                try {
                    tone = new ToneGenerator(AudioManager.STREAM_MUSIC, 85);
                    if ("double".equals(mode)) {
                        tone.startTone(ToneGenerator.TONE_PROP_BEEP, 95);
                        SystemClock.sleep(125L);
                        tone.stopTone();
                        tone.startTone(ToneGenerator.TONE_PROP_BEEP, 95);
                        SystemClock.sleep(105L);
                    } else if ("soft".equals(mode)) {
                        tone.startTone(ToneGenerator.TONE_CDMA_PIP, 150);
                        SystemClock.sleep(165L);
                    } else {
                        tone.startTone(ToneGenerator.TONE_PROP_ACK, 85);
                        SystemClock.sleep(100L);
                    }
                } catch (Throwable error) {
                    Log.w(TAG, "Could not play scan tone", error);
                } finally {
                    if (tone != null) tone.release();
                }
            }
        }, "label-scan-tone");
        toneThread.setDaemon(true);
        toneThread.start();
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

    private static final class BridgeConfig {
        private final String environment;
        private final String developmentUrl;
        private final String testUrl;
        private final String productionUrl;
        private final String scanSound;

        private BridgeConfig(
            String environment,
            String developmentUrl,
            String testUrl,
            String productionUrl,
            String scanSound) {
            this.environment = environment;
            this.developmentUrl = developmentUrl;
            this.testUrl = testUrl;
            this.productionUrl = productionUrl;
            this.scanSound = scanSound;
        }

        private static BridgeConfig defaults() {
            return new BridgeConfig(
                "development",
                DEFAULT_DEVELOPMENT_URL,
                DEFAULT_TEST_URL,
                DEFAULT_PRODUCTION_URL,
                "short");
        }

        private static BridgeConfig fromPreferences(SharedPreferences preferences) {
            return new BridgeConfig(
                environment(preferences.getString("environment", "development")),
                valueOrDefault(preferences.getString("developmentUrl", null), DEFAULT_DEVELOPMENT_URL),
                valueOrDefault(preferences.getString("testUrl", null), DEFAULT_TEST_URL),
                valueOrDefault(preferences.getString("productionUrl", null), DEFAULT_PRODUCTION_URL),
                soundMode(preferences.getString("scanSound", "short")));
        }

        private static BridgeConfig fromParameters(Map<String, String> values, boolean allowDefaults) {
            String rawEnvironment = values.get("environment");
            if (!allowDefaults && !isEnvironment(rawEnvironment)) return null;
            String environment = environment(rawEnvironment);
            String developmentUrl = normalizeUrl(values.get("developmentUrl"));
            String testUrl = normalizeUrl(values.get("testUrl"));
            String productionUrl = normalizeUrl(values.get("productionUrl"));
            String scanSound = soundMode(values.get("scanSound"));
            String legacyTarget = normalizeUrl(values.get("target"));
            if (allowDefaults) {
                if (legacyTarget != null && developmentUrl == null) developmentUrl = legacyTarget;
                if (developmentUrl == null) developmentUrl = DEFAULT_DEVELOPMENT_URL;
                if (testUrl == null) testUrl = DEFAULT_TEST_URL;
                if (productionUrl == null) productionUrl = DEFAULT_PRODUCTION_URL;
            }
            if (developmentUrl == null || testUrl == null || productionUrl == null) return null;
            return new BridgeConfig(environment, developmentUrl, testUrl, productionUrl, scanSound);
        }

        private static String environment(String value) {
            if ("test".equals(value) || "production".equals(value)) return value;
            return "development";
        }

        private static boolean isEnvironment(String value) {
            return "development".equals(value)
                || "test".equals(value)
                || "production".equals(value);
        }

        private static String soundMode(String value) {
            return isSoundMode(value) ? value : "short";
        }

        private static String valueOrDefault(String value, String fallback) {
            String normalized = normalizeUrl(value);
            return normalized == null ? fallback : normalized;
        }

        private String selectedUrl() {
            if ("test".equals(environment)) return testUrl;
            if ("production".equals(environment)) return productionUrl;
            return developmentUrl;
        }

        private String toJson() {
            return "{\"environment\":\"" + escapeJson(environment)
                + "\",\"developmentUrl\":\"" + escapeJson(developmentUrl)
                + "\",\"testUrl\":\"" + escapeJson(testUrl)
                + "\",\"productionUrl\":\"" + escapeJson(productionUrl)
                + "\",\"scanSound\":\"" + escapeJson(scanSound)
                + "\",\"bridgeVersion\":\"" + PROTOCOL_VERSION + "\"}";
        }
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
