/** Parses a .NET TimeSpan in its default "c" format: [d.]hh:mm:ss[.fffffff]. */
export function parseTimeSpan(value: string): number {
  const match = /^(-)?(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d+))?$/.exec(value);
  if (!match) {
    return Number.NaN;
  }
  const [, sign, days = "0", hours = "0", minutes = "0", seconds = "0", fraction = "0"] = match;
  const totalSeconds = Number(days) * 86400 + Number(hours) * 3600 + Number(minutes) * 60 + Number(seconds) + Number(`0.${fraction}`);
  return (sign ? -1 : 1) * totalSeconds * 1000;
}

export function formatDuration(milliseconds: number): string {
  if (Number.isNaN(milliseconds)) {
    return "—";
  }
  const seconds = Math.round(Math.abs(milliseconds) / 1000);
  if (seconds < 60) {
    return `${seconds} s`;
  }
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    const rest = seconds % 60;
    return rest === 0 ? `${minutes} min` : `${minutes} min ${rest} s`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 48) {
    const rest = minutes % 60;
    return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
  }
  return `${Math.floor(hours / 24)} d`;
}

export function formatTimeSpan(value: string): string {
  return formatDuration(parseTimeSpan(value));
}

/** "12 s ago" for the past, "in 25 s" for the future. */
export function formatRelative(iso: string | null | undefined, now: number): string {
  if (!iso) {
    return "—";
  }
  const difference = new Date(iso).getTime() - now;
  if (Math.abs(difference) < 1000) {
    return "now";
  }
  return difference > 0 ? `in ${formatDuration(difference)}` : `${formatDuration(difference)} ago`;
}

export function formatTimestamp(iso: string | null | undefined): string {
  return iso ? new Date(iso).toLocaleString() : "—";
}

export const stateLabels: Record<string, string> = {
  Closed: "Closed",
  Open: "Open",
  HalfOpen: "Half-open",
  Isolated: "Isolated",
  Disabled: "Disabled",
};
