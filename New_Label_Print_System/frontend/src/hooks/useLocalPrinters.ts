import { useQuery } from "@tanstack/react-query";
import { fetchLocalPrinters } from "../lib/localAgent";
import { useMe } from "./useMe";
import { ls, configureStation, station, type Capabilities } from "../lib/labelSystem";

async function readPrinters(): Promise<string[]> {
  const printers = await fetchLocalPrinters();
  return printers.filter((printer) => printer.kind === "local").map((printer) => printer.name);
}

export function useLocalPrinters(options?: { enabled?: boolean }) {
  const { data: me } = useMe();
  return useQuery({
    queryKey: ["this-computer-printers", me?.authentication_source],
    queryFn: async () => {
      if (me?.authentication_source === "labelsystem") {
        configureStation(await ls<Capabilities>("/print/capabilities"));
        return (await station<{ printers: { name: string }[] }>("/printers")).printers.map(
          (p) => p.name,
        );
      }
      return readPrinters();
    },
    enabled: !!me && (options?.enabled ?? true),
    retry: false,
    refetchInterval: 30_000,
  });
}
