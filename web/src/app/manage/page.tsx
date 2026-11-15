import type { Metadata } from "next";
import { ManagePage } from "@/features/manage/ManagePage";

// The link's token is in the query string: never send it on as a referrer (Design §7.3, §18.10).
export const metadata: Metadata = {
  title: "Your booking (demo)",
  referrer: "no-referrer",
};

/** `/manage/?token=…`: a guest's booking from a signed link (Requirements step 5, Design §18.1, §18.10). Static. */
export default function ManageRoute() {
  return <ManagePage />;
}
