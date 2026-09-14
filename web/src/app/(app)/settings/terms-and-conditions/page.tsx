"use client";

import { Suspense, useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import {
  Loader2,
  Pencil,
  Trash2,
  Plus,
  Star,
  ArrowLeft,
  Save,
  Eye,
} from "lucide-react";
import { termsContentApi, type TermsContentItem } from "@/lib/api";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { RichTextEditor } from "@/components/terms/rich-text-editor";

function TermsList({
  items,
  onEdit,
  onDelete,
}: {
  items: TermsContentItem[];
  onEdit: (item: TermsContentItem) => void;
  onDelete: (item: TermsContentItem) => void;
}) {
  const featured = items.filter((i) => i.isSystem);
  const custom = items.filter((i) => !i.isSystem);

  return (
    <div className="space-y-8">
      <div className="flex items-start justify-between">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Terms &amp; Conditions</h1>
          <p className="text-muted-foreground mt-1">
            Manage the T&amp;C content for each tracking feature. Featured terms are built-in
            and cannot be deleted. Custom terms can be created and removed.
          </p>
        </div>
      </div>

      {/* Featured terms */}
      <div className="space-y-4">
        <div className="flex items-center gap-2">
          <h2 className="text-lg font-bold">Featured Terms</h2>
          <Badge variant="secondary" className="text-xs">{featured.length}</Badge>
        </div>
        <div className="space-y-3">
          {featured.map((item) => (
            <Card key={item.id} className="overflow-hidden cursor-pointer hover:border-primary/50 transition-colors" onClick={() => onEdit(item)}>
              <CardContent className="p-4">
                <div className="flex items-center gap-3">
                  <div className="flex-1">
                    <div className="flex items-center gap-2">
                      <h3 className="font-bold">{item.heading}</h3>
                      <Badge variant="secondary" className="text-xs gap-1">
                        <Star className="w-3 h-3" /> Featured
                      </Badge>
                    </div>
                    <p className="text-sm text-muted-foreground mt-1 line-clamp-2">
                      {item.body.replace(/<[^>]*>/g, "").slice(0, 150)}...
                    </p>
                    <div className="flex items-center gap-3 mt-2 text-xs text-muted-foreground">
                      <span>v{item.termsVersion}</span>
                      <span>|</span>
                      <span>Updated {new Date(item.updatedAt).toLocaleDateString()}</span>
                    </div>
                  </div>
                  <Pencil className="h-4 w-4 text-muted-foreground" />
                </div>
              </CardContent>
            </Card>
          ))}
        </div>
      </div>

      {/* Custom terms */}
      <div className="space-y-4">
        <div className="flex items-center gap-2">
          <h2 className="text-lg font-bold">Custom Terms</h2>
          <Badge variant="outline" className="text-xs">{custom.length}</Badge>
        </div>
        {custom.length === 0 ? (
          <div className="rounded-lg border border-dashed p-8 text-center text-sm text-muted-foreground">
            No custom terms created yet. Use the sidebar to add new terms.
          </div>
        ) : (
          <div className="space-y-3">
            {custom.map((item) => (
              <Card key={item.id} className="overflow-hidden cursor-pointer hover:border-primary/50 transition-colors" onClick={() => onEdit(item)}>
                <CardContent className="p-4">
                  <div className="flex items-center gap-3">
                    <div className="flex-1">
                      <div className="flex items-center gap-2">
                        <h3 className="font-bold">{item.heading}</h3>
                        <Badge variant="outline" className="text-xs">Custom</Badge>
                      </div>
                      <p className="text-sm text-muted-foreground mt-1 line-clamp-2">
                        {item.body.replace(/<[^>]*>/g, "").slice(0, 150)}...
                      </p>
                      <div className="flex items-center gap-3 mt-2 text-xs text-muted-foreground">
                        <span>v{item.termsVersion}</span>
                        <span>|</span>
                        <span>Updated {new Date(item.updatedAt).toLocaleDateString()}</span>
                      </div>
                    </div>
                    <div className="flex items-center gap-1" onClick={(e) => e.stopPropagation()}>
                      <Pencil className="h-4 w-4 text-muted-foreground cursor-pointer hover:text-foreground" onClick={() => onEdit(item)} />
                      <Trash2 className="h-4 w-4 text-destructive cursor-pointer hover:text-destructive/80" onClick={() => onDelete(item)} />
                    </div>
                  </div>
                </CardContent>
              </Card>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

function TermsEditor({
  item,
  onBack,
}: {
  item: TermsContentItem | null;
  onBack: () => void;
}) {
  const queryClient = useQueryClient();
  const isNew = item === null;
  const [heading, setHeading] = useState(item?.heading ?? "");
  const [body, setBody] = useState(item?.body ?? "");
  const [version, setVersion] = useState(item?.termsVersion ?? "1.0");

  const updateMutation = useMutation({
    mutationFn: (data: { heading: string; body: string; termsVersion: string }) =>
      termsContentApi.update(item!.id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["terms-content"] });
      onBack();
    },
  });

  const createMutation = useMutation({
    mutationFn: (data: { heading: string; body: string; termsVersion: string }) =>
      termsContentApi.create(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["terms-content"] });
      onBack();
    },
  });

  const handleSave = () => {
    if (!heading.trim()) return;
    const data = { heading, body, termsVersion: version };
    if (isNew) {
      createMutation.mutate(data);
    } else {
      updateMutation.mutate(data);
    }
  };

  const isSaving = updateMutation.isPending || createMutation.isPending;

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <Button variant="ghost" size="sm" onClick={onBack}>
          <ArrowLeft className="h-4 w-4 mr-1" /> Back
        </Button>
        <Separator orientation="vertical" className="h-6" />
        <h1 className="text-2xl font-bold tracking-tight">
          {isNew ? "Create New Terms" : `Edit: ${item?.heading}`}
        </h1>
        {item?.isSystem && (
          <Badge variant="secondary" className="text-xs gap-1">
            <Star className="w-3 h-3" /> Featured
          </Badge>
        )}
      </div>

      <div className="space-y-4">
        <div className="space-y-2">
          <Label className="text-sm font-bold">Heading</Label>
          <Input
            value={heading}
            onChange={(e) => setHeading(e.target.value)}
            placeholder="e.g. Screen Recording Policy"
          />
        </div>

        <div className="space-y-2">
          <Label className="text-sm font-bold">Content</Label>
          <RichTextEditor value={body} onChange={setBody} />
        </div>

        <div className="flex items-center gap-4">
          <div className="space-y-1">
            <Label className="text-sm font-bold">Version</Label>
            <Input
              value={version}
              onChange={(e) => setVersion(e.target.value)}
              className="h-9 w-24"
            />
          </div>
        </div>
      </div>

      <div className="flex items-center gap-2">
        <Button onClick={handleSave} disabled={!heading.trim() || isSaving}>
          {isSaving ? (
            <Loader2 className="h-4 w-4 mr-1 animate-spin" />
          ) : (
            <Save className="h-4 w-4 mr-1" />
          )}
          {isNew ? "Create Terms" : "Save Changes"}
        </Button>
        <Button variant="outline" onClick={onBack}>Cancel</Button>
      </div>
    </div>
  );
}

function TermsPreview({
  item,
  onBack,
}: {
  item: TermsContentItem;
  onBack: () => void;
}) {
  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <Button variant="ghost" size="sm" onClick={onBack}>
          <ArrowLeft className="h-4 w-4 mr-1" /> Back
        </Button>
        <Separator orientation="vertical" className="h-6" />
        <h1 className="text-2xl font-bold tracking-tight">{item.heading}</h1>
        {item.isSystem && (
          <Badge variant="secondary" className="text-xs gap-1">
            <Star className="w-3 h-3" /> Featured
          </Badge>
        )}
      </div>

      <Card>
        <CardContent className="p-6">
          <div
            className="prose prose-sm dark:prose-invert max-w-none"
            dangerouslySetInnerHTML={{ __html: item.body }}
          />
          <div className="flex items-center gap-3 mt-6 pt-4 border-t text-xs text-muted-foreground">
            <span>Terms v{item.termsVersion}</span>
            <span>|</span>
            <span>Updated {new Date(item.updatedAt).toLocaleString()}</span>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

function TermsAndConditionsInner() {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<TermsContentItem | null | "new">(null);
  const [previewing, setPreviewing] = useState<TermsContentItem | null>(null);
  const [deleting, setDeleting] = useState<TermsContentItem | null>(null);

  const contentQuery = useQuery({
    queryKey: ["terms-content"],
    queryFn: () => termsContentApi.list(),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => termsContentApi.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["terms-content"] });
    },
  });

  if (editing !== null || previewing !== null) {
    if (previewing) {
      return <TermsPreview item={previewing} onBack={() => setPreviewing(null)} />;
    }
    return (
      <TermsEditor
        item={editing === "new" ? null : editing}
        onBack={() => setEditing(null)}
      />
    );
  }

  return (
    <>
      <div className="flex items-start justify-between mb-6">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Terms &amp; Conditions</h1>
          <p className="text-muted-foreground mt-1">
            Manage the T&amp;C content for each tracking feature. Click a term to edit,
            or use &quot;New Terms&quot; to create a custom entry.
          </p>
        </div>
        <Button onClick={() => setEditing("new")}>
          <Plus className="w-4 h-4 mr-1" /> New Terms
        </Button>
      </div>

      <Separator className="mb-6" />

      {contentQuery.isLoading ? (
        <div className="flex items-center justify-center min-h-[200px]">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : (
        <TermsList
          items={contentQuery.data?.items ?? []}
          onEdit={(item) => setEditing(item)}
          onDelete={(item) => setDeleting(item)}
        />
      )}

      <AlertDialog open={!!deleting} onOpenChange={() => setDeleting(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete Custom Terms</AlertDialogTitle>
            <AlertDialogDescription>
              Are you sure you want to delete &quot;{deleting?.heading}&quot;? This action cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => {
                if (deleting) deleteMutation.mutate(deleting.id);
                setDeleting(null);
              }}
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
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
