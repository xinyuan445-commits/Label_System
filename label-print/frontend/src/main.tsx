import React from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import zh from "./i18n/zh.json";
import { App } from "./App";
import "./style.css";
i18n.use(initReactI18next).init({ lng: "zh", resources: { zh: { translation: zh } }, interpolation: { escapeValue: false } });
createRoot(document.getElementById("root")!).render(
  <React.StrictMode><QueryClientProvider client={new QueryClient()}><App/></QueryClientProvider></React.StrictMode>);
