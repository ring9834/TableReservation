import { HostPage } from "@/features/host/HostPage";
import { hostAppMetadata, hostAppViewport } from "@/app/hostApp";

// The installable host app (M11c, Design §18.11).
export const metadata = hostAppMetadata;
export const viewport = hostAppViewport;

/** `/host`: floor plan and tonight's bookings (Requirements step 2, Design §18). Needs the demo session. */
export default function HostRoute() {
  return <HostPage />;
}
