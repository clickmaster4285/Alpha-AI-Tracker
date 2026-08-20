'use client';

import ClassifiedItemsTable from '@/components/configuration/ClassifiedItemsTable';
import { monitoringApi, type MonitoredSite } from '@/lib/api';

export default function WebsitesClassification() {
  return (
    <ClassifiedItemsTable<MonitoredSite>
      title="Websites"
      description="Classify the websites employees visit. Domains are auto-discovered from observed browsing activity — nothing here is static."
      queryKeyPrefix="monitoring-websites"
      scope="website"
      nameHeader="Domain"
      nameOf={(item) => item.domain}
      listFn={(params) => monitoringApi.websites.list(params)}
      classifyFn={(id, payload) => monitoringApi.websites.classify(Number(id), payload)}
    />
  );
}