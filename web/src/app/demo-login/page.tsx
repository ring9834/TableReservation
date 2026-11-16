import { DemoLoginForm } from "@/features/demo-login/DemoLoginForm";
import { hostAppMetadata, hostAppViewport } from "@/app/hostApp";

// Part of the installable host app, so signing in never leaves it (M11c, Design §18.11).
export const metadata = hostAppMetadata;
export const viewport = hostAppViewport;

/** `/demo-login`: staff sign-in with a demo passcode (Design §17). Static; everything runs in the browser. */
export default function DemoLoginRoute() {
  return <DemoLoginForm />;
}
