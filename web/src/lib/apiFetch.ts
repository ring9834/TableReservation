// The one fetch wrapper the generated client calls (orval `mutator`, Design §18.4). Not generated: edit freely.

/** The API base URL. Locally the API runs on https://localhost:5001; CI and AWS set NEXT_PUBLIC_API_BASE_URL. */
export const apiBaseUrl = (
  process.env.NEXT_PUBLIC_API_BASE_URL ?? "https://localhost:5001"
).replace(/\/+$/, "");

/** Problem Details from the API (ADR-004, Design §14.1), or a failure that never reached it. */
export class ApiError extends Error {
  constructor(
    /** HTTP status; 0 when the request never got an answer (offline, CORS, TLS). */
    readonly status: number,
    /** Stable error code, e.g. `slot_taken`; `unexpected` for anything without one. */
    readonly code: string,
    /** On 400 `validation_failed`: camelCase field → rule codes (`{ mobile: ["mobile_not_mobile"] }`). */
    readonly fieldErrors: Record<string, string[]> | null = null,
    /** W3C trace ID to quote when reporting a problem. */
    readonly traceId: string | null = null,
  ) {
    super(`API error ${status} ${code}`);
    this.name = "ApiError";
  }
}

/** The error type of every generated hook: always an {@link ApiError}, whatever the operation documents. */
// eslint-disable-next-line @typescript-eslint/no-unused-vars
export type ErrorType<_Documented> = ApiError;

export const isApiError = (error: unknown): error is ApiError => error instanceof ApiError;

const newCorrelationId = () => crypto.randomUUID();

/**
 * Calls the API: base URL, cookies included (the demo session, Design §17), a fresh `X-Correlation-Id` per request,
 * JSON in and out. A non-2xx answer is thrown as {@link ApiError} with the Problem Details `code`.
 */
export const apiFetch = async <T>(url: string, init: RequestInit = {}): Promise<T> => {
  const headers = new Headers(init.headers);
  headers.set("X-Correlation-Id", newCorrelationId());
  headers.set("Accept", "application/json, application/problem+json");

  let response: Response;
  try {
    response = await fetch(`${apiBaseUrl}${url}`, { ...init, headers, credentials: "include" });
  } catch (error) {
    if (init.signal?.aborted) {
      throw error; // a cancelled query, not an API failure
    }

    throw new ApiError(0, "unexpected");
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
};

const toApiError = async (response: Response): Promise<ApiError> => {
  try {
    const problem = (await response.json()) as {
      code?: unknown;
      errors?: unknown;
      traceId?: unknown;
    };
    return new ApiError(
      response.status,
      typeof problem.code === "string" ? problem.code : codeForStatus(response.status),
      isFieldErrors(problem.errors) ? problem.errors : null,
      typeof problem.traceId === "string" ? problem.traceId : null,
    );
  } catch {
    return new ApiError(response.status, codeForStatus(response.status));
  }
};

const codeForStatus = (status: number) => (status === 429 ? "too_many_requests" : "unexpected");

const isFieldErrors = (value: unknown): value is Record<string, string[]> =>
  typeof value === "object" &&
  value !== null &&
  Object.values(value).every((v) => Array.isArray(v) && v.every((c) => typeof c === "string"));
