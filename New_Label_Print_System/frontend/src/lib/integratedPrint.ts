import {
  durable,
  ls,
  sendPrint,
  configureStation,
  station,
  type Capabilities,
  type PrintJob,
  type PendingLabel,
} from "./labelSystem";

export async function sourcePrint(
  kind: "bill" | "stock",
  body: Record<string, unknown>,
  userId: number,
) {
  configureStation(await ls<Capabilities>("/print/capabilities"));
  const helper = await station();
  if (!helper.print_dispatch_enabled) throw new Error("请启用本机打印助手。");
  body = { ...body, deviceId: helper.deviceId };
  const identity = kind === "bill" ? `${body.bill_type}:${body.bill_id}` : "stock";
  const source = await durable(`${userId}:source-${kind}:${identity}`, body, (request) =>
    ls<{
      operationId: number;
      targets: { labelId: number; originLineId: number; expectedRowVersion: string }[];
      templateVersionId: number;
      deviceId: string;
      printer: string;
    }>(`/source-print/${kind}`, request),
  );
  const jobs: PrintJob[] = [];
  for (let offset = 0; offset < source.targets.length; offset += 100) {
    const current = await ls<PendingLabel[]>("/print/pending-labels");
    const targets = source.targets.slice(offset, offset + 100).map((t) => {
      const row = current.find((p) => p.labelId === t.labelId);
      return { ...t, expectedRowVersion: row?.rowVersion ?? t.expectedRowVersion };
    });
    jobs.push(
      await durable(
        `${userId}:first:source:${source.operationId}:${offset}`,
        {
          targets,
          templateVersionId: source.templateVersionId,
          deviceId: source.deviceId,
          printer: source.printer,
        },
        (request) => ls<PrintJob>("/print/jobs/first", request),
      ),
    );
  }
  let printed = 0;
  try {
    for (const job of jobs) {
      const complete = await sendPrint(job);
      if (complete.phase !== "COMPLETED") throw new Error("登记待恢复，请到打印记录恢复原任务。");
      printed += complete.outputs.length;
    }
  } catch (error) {
    throw new Error(
      (error instanceof Error ? error.message : "打印结果待核对。") + " 请到“打印记录”查询原任务。",
    );
  }
  return {
    printed,
    output_dir: body.printer === "Microsoft Print to PDF" ? "本机“下载/标签打印”" : null,
  };
}
