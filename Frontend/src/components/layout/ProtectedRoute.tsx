import { Navigate } from 'react-router-dom';
import { useAuth } from '@/lib/auth';
import { usePermissions } from '@/lib/permissions';

interface ProtectedRouteProps {
  children: React.ReactNode;
  module?: string;
}

export default function ProtectedRoute({ children, module }: ProtectedRouteProps) {
  const { user } = useAuth();
  const { canAccess } = usePermissions();

  if (!user) return <Navigate to="/login" replace />;
  if (module && !canAccess(user.role, module)) {
    return (
      <div className="flex-1 flex items-center justify-center p-8">
        <div className="text-center">
          <div className="w-16 h-16 rounded-full bg-destructive/10 flex items-center justify-center mx-auto mb-4">
            <span className="text-2xl">🔒</span>
          </div>
          <h2 className="text-xl font-display font-bold text-foreground mb-2">Access Denied</h2>
          <p className="text-muted-foreground text-sm">You don't have permission to access this module.</p>
        </div>
      </div>
    );
  }

  return <>{children}</>;
}
