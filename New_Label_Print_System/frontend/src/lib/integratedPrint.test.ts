import { expect, it } from "vitest";
import { sourceReprintRequest } from "./integratedPrint";

it("reuses the original label as a zero-quantity reprint request", () => {
  expect(
    sourceReprintRequest(
      { labelId: 42, expectedRowVersion: "row-version", copies: 3 },
      { deviceId: "device", printer: "Microsoft Print to PDF" },
    ),
  ).toEqual({
    labelId: 42,
    expectedRowVersion: "row-version",
    copies: 3,
    reason: "单据页面再次打印",
    deviceId: "device",
    printer: "Microsoft Print to PDF",
  });
});
