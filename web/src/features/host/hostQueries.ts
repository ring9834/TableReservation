import type { QueryClient } from "@tanstack/react-query";
import { getGetTonightBookingsQueryKey } from "@/api/endpoints/bookings/bookings";
import { getGetFloorPlanQueryKey } from "@/api/endpoints/floor-plan/floor-plan";
import { getGetTableSuggestionQueryKey } from "@/api/endpoints/seating/seating";
import {
  getGetQuotedWaitQueryKey,
  getGetWaitlistQueryKey,
} from "@/api/endpoints/waitlist/waitlist";

/**
 * Refetches everything a host action can change: floor plan, tonight's list, the waitlist (any `tableId`), table
 * suggestions and quotes (any party size). Called after every action and after a "this has just changed" error, so
 * the screen never waits for the SignalR notice to show its own change (Design §18.6).
 */
export const invalidateHostQueries = (queryClient: QueryClient, venueId: string) =>
  Promise.all([
    queryClient.invalidateQueries({ queryKey: getGetFloorPlanQueryKey(venueId) }),
    queryClient.invalidateQueries({ queryKey: getGetTonightBookingsQueryKey(venueId) }),
    queryClient.invalidateQueries({ queryKey: getGetWaitlistQueryKey(venueId) }),
    queryClient.invalidateQueries({ queryKey: getGetTableSuggestionQueryKey(venueId) }),
    queryClient.invalidateQueries({ queryKey: getGetQuotedWaitQueryKey(venueId) }),
  ]);
