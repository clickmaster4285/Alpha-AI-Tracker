"use client";

import { useEffect } from "react";
import { useRouter, usePathname } from "next/navigation";
import { useAuth } from "@/lib/auth";
import { usePermissions } from "@/lib/permissions";
import { findModuleForPath } from "./AppSidebar";

// RouteGuard enforces the logged-in user's role permissions on every (app)
// route: if the pathname's module is not granted by the role's submodule set,
// the user is redirected to /unauthorized. Unguarded paths pass through.
export default function RouteGuard({ children }: { children: React.ReactNode }) {
  const { user, isLoading } = useAuth();
  const { canAccess } = usePermissions();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (isLoading || !user || !pathname) return;
    const module = findModuleForPath(pathname);
    if (module && !canAccess(user.role, module)) {
      router.replace("/unauthorized");
    }
  }, [isLoading, user, pathname, canAccess, router]);

  return <>{children}</>;
}
