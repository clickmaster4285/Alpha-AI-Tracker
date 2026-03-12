import { useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, Shield, Edit2, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';

const initialRules = [
  { id: '1', name: 'Block USB File Transfer', trigger: 'USB', pattern: '*.xlsx, *.csv, *.pdf', action: 'Block', severity: 'Critical', applyTo: ['All Departments'] },
  { id: '2', name: 'Alert on Cloud Upload', trigger: 'Cloud Upload', pattern: '*.zip, *.tar, *.env', action: 'Alert + Block', severity: 'High', applyTo: ['Engineering', 'IT'] },
  { id: '3', name: 'Monitor Email Attachments', trigger: 'Email', pattern: 'confidential, secret, internal', action: 'Alert Only', severity: 'Medium', applyTo: ['All Departments'] },
];

const severityColors: Record<string, string> = {
  Critical: 'bg-destructive/15 text-destructive',
  High: 'bg-warning/15 text-warning',
  Medium: 'bg-info/15 text-info',
  Low: 'bg-muted text-muted-foreground',
};

export default function DLPRulesPage() {
  const [rules, setRules] = useState(initialRules);
  const [showDialog, setShowDialog] = useState(false);

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex items-center justify-between">
        <h3 className="font-display font-bold text-lg text-foreground">DLP Rules</h3>
        <Button onClick={() => setShowDialog(true)} size="sm" className="gap-1 gradient-primary text-primary-foreground"><Plus className="w-4 h-4" /> Add Rule</Button>
      </div>

      <div className="space-y-3">
        {rules.map((rule, i) => (
          <motion.div key={rule.id} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }} className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all">
            <div className="flex items-start justify-between">
              <div className="flex items-center gap-3">
                <Shield className="w-5 h-5 text-primary flex-shrink-0" />
                <div>
                  <h4 className="font-display font-bold text-foreground">{rule.name}</h4>
                  <p className="text-xs text-muted-foreground mt-0.5">Trigger: {rule.trigger} • Pattern: <code className="bg-muted px-1 rounded text-[10px]">{rule.pattern}</code></p>
                </div>
              </div>
              <div className="flex items-center gap-2">
                <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${severityColors[rule.severity]}`}>{rule.severity}</span>
                <span className="px-2.5 py-1 rounded-full text-xs font-medium bg-accent text-accent-foreground">{rule.action}</span>
                <button className="p-1.5 rounded hover:bg-muted"><Edit2 className="w-3.5 h-3.5 text-muted-foreground" /></button>
                <button onClick={() => setRules(rules.filter(r => r.id !== rule.id))} className="p-1.5 rounded hover:bg-destructive/10"><Trash2 className="w-3.5 h-3.5 text-destructive" /></button>
              </div>
            </div>
            <div className="flex gap-1 mt-3">{rule.applyTo.map(a => (
              <span key={a} className="px-2 py-0.5 rounded bg-muted text-muted-foreground text-[10px] font-medium">{a}</span>
            ))}</div>
          </motion.div>
        ))}
      </div>

      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">New DLP Rule</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <Input placeholder="Rule Name *" />
            <Select defaultValue="USB"><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="File Transfer">File Transfer</SelectItem><SelectItem value="USB">USB</SelectItem><SelectItem value="Cloud Upload">Cloud Upload</SelectItem><SelectItem value="Email">Email</SelectItem></SelectContent></Select>
            <Input placeholder="Pattern / Keyword (regex supported)" />
            <div className="grid grid-cols-2 gap-3">
              <Select defaultValue="Alert Only"><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="Alert Only">Alert Only</SelectItem><SelectItem value="Block">Block</SelectItem><SelectItem value="Alert + Block">Alert + Block</SelectItem></SelectContent></Select>
              <Select defaultValue="High"><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="Critical">Critical</SelectItem><SelectItem value="High">High</SelectItem><SelectItem value="Medium">Medium</SelectItem><SelectItem value="Low">Low</SelectItem></SelectContent></Select>
            </div>
            <Button className="w-full gradient-primary text-primary-foreground">Create Rule</Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
