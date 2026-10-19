import { NavLink } from "react-router-dom";
import { useMe } from "../hooks/useMe";

export function PrintPageTabs() {
  const { data: me } = useMe();
  const tab = (to: string, label: string) => (
    <NavLink
      to={to}
      className={({ isActive }) =>
        [
          "rounded-md px-4 py-2 text-sm font-medium",
          isActive
            ? "bg-indigo-600 text-white"
            : "border border-slate-700 text-slate-300 hover:bg-slate-800",
        ].join(" ")
      }
    >
      {label}
    </NavLink>
  );
  return (
    <nav aria-label="打印方式" className="flex shrink-0 flex-wrap gap-2">
      {tab("/bills", "单据打印")}
      {tab("/stock", "存货打印")}
      {tab("/reprint", "标签补打")}
      {me?.authentication_source === "labelsystem" && tab("/print-records", "打印记录")}
    </nav>
  );
}
