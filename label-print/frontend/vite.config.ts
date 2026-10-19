import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
export default defineConfig({ base: "/printing/", plugins: [react()], build: { sourcemap: false } });
