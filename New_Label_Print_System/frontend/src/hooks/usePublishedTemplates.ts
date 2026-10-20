import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ls, type PrintTemplate } from "../lib/labelSystem";

type FormalTemplateDetail = {
  draft_canvas_json: string;
  is_enabled: boolean;
};

type SourceDesigner = {
  system?: string;
  templateId?: number;
};

export type FormalTemplate = PrintTemplate & {
  enabled: boolean;
  sourceTemplateId: number | null;
};

const KEY = ["formal-templates"] as const;

async function enrich(template: PrintTemplate, enabled: boolean): Promise<FormalTemplate> {
  const detail = await ls<FormalTemplateDetail>(`/print/templates/${template.id}`);
  let sourceTemplateId: number | null = null;
  try {
    const canvas = JSON.parse(detail.draft_canvas_json) as { sourceDesigner?: SourceDesigner };
    const source = canvas.sourceDesigner;
    if (source?.system === "New_Label_Print_System" && Number.isInteger(source.templateId)) {
      sourceTemplateId = source.templateId ?? null;
    }
  } catch {
    // Templates created outside this designer remain manageable as formal templates.
  }
  return { ...template, enabled, sourceTemplateId };
}

async function loadFormalTemplates(): Promise<FormalTemplate[]> {
  const [active, archived] = await Promise.all([
    ls<PrintTemplate[]>("/print/templates"),
    ls<PrintTemplate[]>("/print/templates/archived"),
  ]);
  return Promise.all([
    ...active.map((template) => enrich(template, true)),
    ...archived.map((template) => enrich(template, false)),
  ]);
}

export function usePublishedTemplates(enabled = true) {
  return useQuery({ queryKey: KEY, queryFn: loadFormalTemplates, enabled });
}

export function useSetPublishedTemplateState() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ template, enabled }: { template: FormalTemplate; enabled: boolean }) =>
      ls<PrintTemplate>(
        `/print/templates/${template.id}/state`,
        { expectedRowVersion: template.rowVersion, enabled },
        "PUT",
      ),
    onSuccess: () => client.invalidateQueries({ queryKey: KEY }),
  });
}

export function useRefreshPublishedTemplates() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: KEY });
}
