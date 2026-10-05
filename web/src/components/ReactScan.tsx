"use client";

// Must load before React so renders are instrumented from the first paint.
import { scan } from "react-scan";

scan({
  enabled: process.env.NODE_ENV === "development",
  showToolbar: true,
});

/** Dev-only render highlighter. No-op in production. */
export function ReactScan() {
  return null;
}
