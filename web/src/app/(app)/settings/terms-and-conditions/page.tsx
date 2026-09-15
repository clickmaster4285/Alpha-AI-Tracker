"use client";

import { Suspense, useState } from "react";
import { useRouter } from "next/navigation";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Loader2, Pencil, Trash2, Plus, Star, ChevronRight } from "lucide-react";
import { termsContentApi, type TermsContentItem } from "@/lib/api";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useUrlQueryState } from "@/hooks/use-url-query-state";
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

type TermTab = "featured" | "custom";

function TermCard({ item, onOpen }: { item: TermsContentItem; onOpen: (item: TermsContentItem) => void }) {
  return (
    <Card
      className="overflow-hidden cursor-pointer hover:border-primary/50 transition-colors"
      onClick={() => onOpen(item)}
    >
      <CardContent className="p-4">
        <div className="flex items-center gap-3">
          <div className="flex-1">
            <div className="flex items-center gap-2">
              <h3 className="font-bold truncate">{item.heading}</h3>
              {item.isSystem ? (
                <Badge variant="secondary" className="text-xs gap-1 shrink-0">
                  <Star className="w-3 h-3" /> Featured
                </Badge>
              ) : (
                <Badge variant="outline" className="text-xs shrink-0">Custom</Badge>
              )}
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
          <ChevronRight className="h-4 w-4 text-muted-foreground shrink-0" />
        </div>
      </CardContent>
    </Card>
  );
}
function TermsList({
  items,
  onOpen,
  onEdit,
  onDelete,
}: {
  items: TermsContentItem[];
  onOpen: (item: TermsContentItem) => void;
  onEdit: (item: TermsContentItem) => void;
  onDelete: (item: TermsContentItem) => void;
}) {
  return (
    <div className="space-y-3">
      {items.length === 0 ? (
        <div className="rounded-lg border border-dashed p-10 text-center text-sm text-muted-foreground">
          No terms in this category.
        </div>
      ) : (
        items.map((item) => (
          <div key={item.id} className="flex items-start gap-2">
            <div className="flex-1">
              <TermCard item={item} onOpen={onOpen} />
            </div>
            <div className="flex flex-col items-center gap-2 w-8">
              <Pencil
                className="h-4 w-4 text-muted-foreground cursor-pointer hover:text-foreground"
                onClick={() => onEdit(item)}
              />
              {!item.isSystem && (
                <Trash2
                  className="h-4 w-4 text-destructive cursor-pointer hover:text-destructive/80"
                  onClick={() => onDelete(item)}
                />
              )}
            </div>
          </div>
        ))
      )}
    </div>
  );
}
function TermsAndConditionsInner() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [deleting, setDeleting] = useState<TermsContentItem | null>(null);

  const [urlState, setUrlState] = useUrlQueryState<{ tab: TermTab }>(
    { tab: { parse: (raw) => (raw === "custom" ? "custom" : "featured") } },
    { tab: "featured" },
  );

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

  const items = contentQuery.data?.items ?? [];
  const featured = items.filter((i) => i.isSystem);
  const custom = items.filter((i) => !i.isSystem);

  return (
    <>
      <div className="flex items-start justify-between mb-6">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Terms &amp; Conditions</h1>
          <p className="text-muted-foreground mt-1">
            Manage the T&amp;C content for each tracking feature. Select a category to review its terms.
          </p>
        </div>
        <Button onClick={() => router.push("/settings/terms-and-conditions/create")}>
          <Plus className="w-4 h-4 mr-1" /> New Terms
        </Button>
      </div>

      <Separator className="mb-6" />

      {contentQuery.isLoading ? (
        <div className="flex items-center justify-center min-h-[200px]">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : (
        <Tabs value={urlState.tab} onValueChange={(value) => setUrlState({ tab: value as TermTab })}>
          <TabsList>
            <TabsTrigger value="featured">
              Featured Terms
              <Badge variant="secondary" className="ml-1.5 text-[10px]">{featured.length}</Badge>
            </TabsTrigger>
            <TabsTrigger value="custom">
              Custom Terms
              <Badge variant="outline" className="ml-1.5 text-[10px]">{custom.length}</Badge>
            </TabsTrigger>
          </TabsList>
          <TabsContent value="featured" className="mt-4">
            <TermsList
              items={featured}
              onOpen={(item) => router.push(`/settings/terms-and-conditions/view/${item.id}`)}
              onEdit={(item) => router.push(`/settings/terms-and-conditions/edit/${item.id}`)}
              onDelete={(item) => setDeleting(item)}
            />
          </TabsContent>
          <TabsContent value="custom" className="mt-4">
            <TermsList
              items={custom}
              onOpen={(item) => router.push(`/settings/terms-and-conditions/view/${item.id}`)}
              onEdit={(item) => router.push(`/settings/terms-and-conditions/edit/${item.id}`)}
              onDelete={(item) => setDeleting(item)}
            />
          </TabsContent>
        </Tabs>
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