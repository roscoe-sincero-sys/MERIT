/** One accessible confirmation dialog shared with the server-rendered shell; unavailable shell fails closed. */
declare global {
  interface Window { meritConfirm?: (message: string, acceptLabel?: string) => Promise<boolean>; }
}
export function confirmAction(message: string, acceptLabel = 'Continue'): Promise<boolean> {
  return window.meritConfirm ? window.meritConfirm(message, acceptLabel) : Promise.resolve(false);
}
