"use client";

import { Suspense, useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import {
  Loader2,
  Shield,
  Globe,
  FolderOpen,
  MonitorPlay,
  Eye,
  CheckCircle2,
  AlertCircle,
} from "lucide-react";
import { termsConsentApi, type TermsConsentEntry } from "@/lib/api";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";

const FEATURES = [
  {
    id: "app_usage",
    title: "Application Usage Tracking",
    icon: <MonitorPlay className="w-5 h-5" />,
    required: true,
    sections: [
      {
        heading: "What We Track",
        body: "Alpha AI Tracker monitors which desktop applications are open and actively used on your work machine. This includes the application name, process name, and the duration each application remains in focus.",
      },
      {
        heading: "How This Data Is Used",
        body: "Application usage data is used to generate productivity reports and help managers understand how time is allocated across tools and workflows. It is not used for punitive purposes.",
      },
      {
        heading: "Data Retention",
        body: "Application usage records are retained on the server for the period configured by your organization's administrator (default: 30 days). After this period, session data is automatically purged.",
      },
      {
        heading: "Your Rights",
        body: "This feature is required for the Alpha AI Tracker to function. You cannot revoke consent for application usage tracking while the tracker is active on your machine.",
      },
    ],
  },
  {
    id: "browser_journey",
    title: "Browser Journey Tracking",
    icon: <Globe className="w-5 h-5" />,
    required: false,
    sections: [
      {
        heading: "What We Track",
        body: "When enabled, Alpha AI Tracker records the web pages you visit in supported browsers (Chrome, Firefox, Edge, Brave, Opera, and others). This includes the page URL, page title, and the time spent on each page.",
      },
      {
        heading: "How This Data Is Used",
        body: "Browser journey data helps your organization understand which websites and web applications are used for work. It can identify training needs, blocked resources, or productivity patterns.",
      },
      {
        heading: "Incognito / Private Browsing",
        body: "By default, incognito and private browsing windows are NOT tracked. If your organization enables incognito capture, a separate consent prompt will appear before any private browsing data is collected.",
      },
      {
        heading: "Data Retention",
        body: "Browser journey records are retained on the server for the period configured by your organization's administrator (default: 30 days). After this period, journey data is automatically purged.",
      },
      {
        heading: "Your Rights",
        body: "You may revoke consent for browser journey tracking at any time. Revoking consent will stop future data collection for this feature. Previously collected data will be retained until it expires under the organization's data retention policy.",
      },
    ],
  },
  {
    id: "file_journey",
    title: "File Explorer Journey Tracking",
    icon: <FolderOpen className="w-5 h-5" />,
    required: false,
    sections: [
      {
        heading: "What We Track",
        body: "When enabled, Alpha AI Tracker monitors file manager activity on your machine. This includes which folders you navigate to, and any files you create, rename, or delete through the file explorer.",
      },
      {
        heading: "How This Data Is Used",
        body: "File journey data helps understand file organization patterns and collaboration workflows. It is used to generate insights about how your team manages documents and project files.",
      },
      {
        heading: "What We Do NOT Track",
        body: "This feature does not read file contents, track file modifications made through applications (e.g., saving in VS Code or Word), or access files outside the directories you actively browse.",
      },
      {
        heading: "Data Retention",
        body: "File journey records are retained on the server for the period configured by your organization's administrator (default: 30 days). After this period, journey data is automatically purged.",
      },
      {
        heading: "Your Rights",
        body: "You may revoke consent for file journey tracking at any time. Revoking consent will stop future data collection for this feature. Previously collected data will be retained until it expires under the organization's data retention policy.",
      },
    ],
  },
  {
    id: "live_view",
    title: "Live Screen Viewing",
    icon: <Eye className="w-5 h-5" />,
    required: false,
    sections: [
      {
        heading: "What This Feature Does",
        body: "When enabled, authorized administrators can view your screen in real-time. This feature is intended for remote support, training, and collaboration scenarios.",
      },
      {
        heading: "How This Data Is Used",
        body: "Live screen viewing is used only when an administrator explicitly initiates a viewing session. Your screen is not continuously recorded or streamed without your knowledge.",
      },
      {
        heading: "Notification",
        body: "When an administrator initiates a live viewing session, you will receive a notification on your desktop. You will always know when your screen is being viewed.",
      },
      {
        heading: "Your Rights",
        body: "You may revoke consent for live screen viewing at any time. Revoking consent will immediately terminate any active viewing session and prevent future sessions until you re-consent.",
      },
    ],
  },
];

function TermsAndConditionsInner() {
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

  const consentQuery = useQuery({
    queryKey: ["terms-consent", employeeId],
    queryFn: () => termsConsentApi.list(employeeId),
    enabled: !!employeeId,
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

  return (
    <div className="space-y-8">
      <div>
        <h1 className="text-3xl font-bold tracking-tight">Terms &amp; Conditions</h1>
        <p className="text-muted-foreground mt-1">
          Review the terms for each tracking feature in the Alpha AI Tracker. Your consent
          status is shown below each feature description.
        </p>
      </div>

      <Separator />

      <div className="space-y-6">
        {FEATURES.map((feature) => {
          const consent = getConsentStatus(feature.id);
          const isAccepted = consent?.action === "accepted" || consent?.action === "re_accepted";
          const isRevoked = consent?.action === "revoked";

          return (
            <Card key={feature.id} className="overflow-hidden">
              <CardContent className="p-0">
                <div className="flex items-center gap-3 px-6 py-4 bg-muted/40">
                  <div className="flex items-center justify-center w-9 h-9 rounded-lg bg-background border">
                    {feature.icon}
                  </div>
                  <div className="flex-1">
                    <h2 className="text-xl font-bold tracking-tight">
                      {feature.title}
                    </h2>
                  </div>
                  <div className="flex items-center gap-2">
                    {feature.required && (
                      <Badge variant="secondary" className="text-xs font-semibold">
                        Required
                      </Badge>
                    )}
                    {!feature.required && (
                      <Badge variant="outline" className="text-xs font-semibold">
                        Optional
                      </Badge>
                    )}
                    {isAccepted && (
                      <Badge
                        variant="default"
                        className="gap-1 text-xs font-semibold bg-green-600 hover:bg-green-700"
                      >
                        <CheckCircle2 className="w-3 h-3" />
                        Accepted
                      </Badge>
                    )}
                    {isRevoked && (
                      <Badge variant="destructive" className="gap-1 text-xs font-semibold">
                        <AlertCircle className="w-3 h-3" />
                        Revoked
                      </Badge>
                    )}
                    {!consent && (
                      <Badge variant="outline" className="text-xs font-semibold text-muted-foreground">
                        Not yet presented
                      </Badge>
                    )}
                  </div>
                </div>

                <div className="px-6 py-5 space-y-5">
                  {feature.sections.map((section, i) => (
                    <div key={i} className="space-y-1.5">
                      <h3 className="text-base font-bold text-foreground">
                        {section.heading}
                      </h3>
                      <p className="text-sm leading-relaxed text-muted-foreground">
                        {section.body}
                      </p>
                    </div>
                  ))}

                  {consent && (
                    <div className="flex items-center gap-3 pt-2 text-xs text-muted-foreground border-t">
                      <span>Terms v{consent.termsVersion}</span>
                      <span className="text-border">|</span>
                      <span>{new Date(consent.createdAt).toLocaleString()}</span>
                    </div>
                  )}
                </div>
              </CardContent>
            </Card>
          );
        })}
      </div>

      <div className="rounded-lg border bg-muted/50 p-4 text-sm text-muted-foreground">
        <p>
          To change your consent preferences, open the Alpha AI Tracker desktop application.
          The T&C modals will appear when features are enabled for the first time or when
          terms are updated.
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
