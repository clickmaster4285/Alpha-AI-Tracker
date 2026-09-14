"use client";

import { Suspense, useEffect, useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import {
  Loader2,
  Globe,
  FolderOpen,
  MonitorPlay,
  Eye,
  CheckCircle2,
  AlertCircle,
  Pencil,
  Save,
  X,
  RotateCcw,
} from "lucide-react";
import { termsConsentApi, termsContentApi, type TermsConsentEntry, type TermsContentItem } from "@/lib/api";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";

const FEATURE_ICONS: Record<string, React.ReactNode> = {
  app_usage: <MonitorPlay className="w-5 h-5" />,
  browser_journey: <Globe className="w-5 h-5" />,
  file_journey: <FolderOpen className="w-5 h-5" />,
  live_view: <Eye className="w-5 h-5" />,
};

const FEATURE_DEFAULTS: Record<string, { heading: string; body: string; required: boolean }> = {
  app_usage: {
    heading: "Application Usage Tracking",
    body: "Alpha AI Tracker monitors which desktop applications are open and actively used on your work machine. This includes the application name, process name, and the duration each application remains in focus.",
    required: true,
  },
  browser_journey: {
    heading: "Browser Journey Tracking",
    body: "When enabled, Alpha AI Tracker records the web pages you visit in supported browsers (Chrome, Firefox, Edge, Brave, Opera, and others). This includes the page URL, page title, and the time spent on each page.",
    required: false,
  },
  file_journey: {
    heading: "File Explorer Journey Tracking",
    body: "When enabled, Alpha AI Tracker monitors file manager activity on your machine. This includes which folders you navigate to, and any files you create, rename, or delete through the file explorer.",
    required: false,
  },
  live_view: {
    heading: "Live Screen Viewing",
    body: "When enabled, authorized administrators can view your screen in real-time. This feature is intended for remote support, training, and collaboration scenarios.",
    required: false,
  },
};

function FeatureCard({
  content,
  consent,
  onSave,
}: {
  content: TermsContentItem | null;
  consent: TermsConsentEntry | null;
  onSave: (featureId: string, heading: string, body: string, version: string) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [heading, setHeading] = useState(content?.heading ?? "");
  const [body, setBody] = useState(content?.body ?? "");
  const [version, setVersion] = useState(content?.termsVersion ?? "1.0");

  const featureId = content?.featureId ?? "";
  const defaults = FEATURE_DEFAULTS[featureId];
  const isAccepted = consent?.action === "accepted" || consent?.action === "re_accepted";
  const isRevoked = consent?.action === "revoked";

  const handleSave = () => {
    onSave(featureId, heading, body, version);
    setEditing(false);
  };

  const handleCancel = () => {
    setHeading(content?.heading ?? defaults?.heading ?? "");
    setBody(content?.body ?? defaults?.body ?? "");
    setVersion(content?.termsVersion ?? "1.0");
    setEditing(false);
  };

  const handleReset = () => {
    setHeading(defaults?.heading ?? "");
    setBody(defaults?.body ?? "");
    setVersion("1.0");
  };

  return (
    <Card className="overflow-hidden">
      <CardContent className="p-0">
        {/* Header bar */}
        <div className="flex items-center gap-3 px-6 py-4 bg-muted/40">
          <div className="flex items-center justify-center w-9 h-9 rounded-lg bg-background border">
            {FEATURE_ICONS[featureId] ?? <MonitorPlay className="w-5 h-5" />}
          </div>
          <div className="flex-1">
            {editing ? (
              <Input
                value={heading}
                onChange={(e) => setHeading(e.target.value)}
                className="h-8 text-lg font-bold"
              />
            ) : (
              <h2 className="text-xl font-bold tracking-tight">{content?.heading ?? defaults?.heading ?? featureId}</h2>
            )}
          </div>
          <div className="flex items-center gap-2">
            {defaults?.required && (
              <Badge variant="secondary" className="text-xs font-semibold">Required</Badge>
            )}
            {!defaults?.required && (
              <Badge variant="outline" className="text-xs font-semibold">Optional</Badge>
            )}
            {isAccepted && (
              <Badge variant="default" className="gap-1 text-xs font-semibold bg-green-600 hover:bg-green-700">
                <CheckCircle2 className="w-3 h-3" /> Accepted
              </Badge>
            )}
            {isRevoked && (
              <Badge variant="destructive" className="gap-1 text-xs font-semibold">
                <AlertCircle className="w-3 h-3" /> Revoked
              </Badge>
            )}
            {!consent && (
              <Badge variant="outline" className="text-xs font-semibold text-muted-foreground">Not yet presented</Badge>
            )}
            {!editing ? (
              <Button variant="ghost" size="sm" onClick={() => setEditing(true)}>
                <Pencil className="w-4 h-4" />
              </Button>
            ) : (
              <>
                <Button variant="ghost" size="sm" onClick={handleCancel}>
                  <X className="w-4 h-4" />
                </Button>
                <Button variant="ghost" size="sm" onClick={handleReset} title="Reset to default">
                  <RotateCcw className="w-4 h-4" />
                </Button>
                <Button variant="default" size="sm" onClick={handleSave}>
                  <Save className="w-4 h-4 mr-1" /> Save
                </Button>
              </>
            )}
          </div>
        </div>

        {/* Content */}
        <div className="px-6 py-5 space-y-4">
          {editing ? (
            <>
              <div className="space-y-2">
                <Label className="text-sm font-bold">Terms Content</Label>
                <Textarea
                  value={body}
                  onChange={(e) => setBody(e.target.value)}
                  rows={6}
                  className="resize-y"
                />
              </div>
              <div className="flex items-center gap-2">
                <Label className="text-sm font-bold">Terms Version</Label>
                <Input
                  value={version}
                  onChange={(e) => setVersion(e.target.value)}
                  className="h-8 w-24"
                />
              </div>
            </>
          ) : (
            <p className="text-sm leading-relaxed text-muted-foreground">
              {content?.body ?? defaults?.body ?? "No content configured."}
            </p>
          )}

          {content && !editing && (
            <div className="flex items-center gap-3 pt-2 text-xs text-muted-foreground border-t">
              <span>Terms v{content.termsVersion}</span>
              <span className="text-border">|</span>
              <span>Updated {new Date(content.updatedAt).toLocaleString()}</span>
            </div>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

function TermsAndConditionsInner() {
  const queryClient = useQueryClient();
  const [employeeId, setEmployeeId] = useState<string>("");

  const profileQuery = useQuery({
    queryKey: ["auth", "profile"],
    queryFn: async () => {
      const res = await fetch("/api/v1/auth/profile", { credentials: "include" });
      if (!res.ok) return null;
      return res.json();
    },
  });

  useEffect(() => {
    const empId = profileQuery.data?.employee?.employeeId;
    if (empId) setEmployeeId(empId);
  }, [profileQuery.data]);

  const contentQuery = useQuery({
    queryKey: ["terms-content"],
    queryFn: () => termsContentApi.list(),
  });

  const consentQuery = useQuery({
    queryKey: ["terms-consent", employeeId],
    queryFn: () => termsConsentApi.list(employeeId),
    enabled: !!employeeId,
  });

  const updateMutation = useMutation({
    mutationFn: (data: { featureId: string; heading: string; body: string; termsVersion: string }) =>
      termsContentApi.update(data.featureId, {
        heading: data.heading,
        body: data.body,
        termsVersion: data.termsVersion,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["terms-content"] });
    },
  });

  const getConsentStatus = (featureId: string): TermsConsentEntry | null => {
    const entries = consentQuery.data?.entries ?? [];
    let latest: TermsConsentEntry | null = null;
    for (const entry of entries) {
      if (entry.featureId === featureId) {
        if (!latest || new Date(entry.createdAt) > new Date(latest.createdAt)) {
          latest = entry;
        }
      }
    }
    return latest;
  };

  const getContent = (featureId: string): TermsContentItem | null => {
    const items = contentQuery.data?.items ?? [];
    return items.find((i) => i.featureId === featureId) ?? null;
  };

  const handleSave = (featureId: string, heading: string, body: string, version: string) => {
    updateMutation.mutate({ featureId, heading, body, termsVersion: version });
  };

  const featureIds = ["app_usage", "browser_journey", "file_journey", "live_view"];

  return (
    <div className="space-y-8">
      <div>
        <h1 className="text-3xl font-bold tracking-tight">Terms &amp; Conditions</h1>
        <p className="text-muted-foreground mt-1">
          Manage the Terms &amp; Conditions content for each tracking feature. Click the edit
          icon to modify the heading, body text, and version number.
        </p>
      </div>

      <Separator />

      {contentQuery.isLoading ? (
        <div className="flex items-center justify-center min-h-[200px]">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : (
        <div className="space-y-6">
          {featureIds.map((featureId) => (
            <FeatureCard
              key={featureId}
              content={getContent(featureId)}
              consent={getConsentStatus(featureId)}
              onSave={handleSave}
            />
          ))}
        </div>
      )}

      <div className="rounded-lg border bg-muted/50 p-4 text-sm text-muted-foreground">
        <p>
          Changes take effect immediately. The desktop client fetches the latest terms content
          when displaying T&C modals to employees.
        </p>
      </div>
    </div>
  );
}

export default function TermsAndConditionsPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[300px]">
          <Loader2 className="w-7 h-7 animate-spin text-primary" />
        </div>
      }
    >
      <TermsAndConditionsInner />
    </Suspense>
  );
}
