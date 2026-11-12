import { DemoLoginForm } from "@/features/demo-login/DemoLoginForm";

/** `/demo-login`: staff sign-in with a demo passcode (Design §17). Static; everything runs in the browser. */
export default function DemoLoginRoute() {
  return <DemoLoginForm />;
}
