let counter = 0

/** Local id for a message created in the browser. `crypto.randomUUID` is missing on plain-HTTP origins. */
export function newLocalId(prefix: string): string {
  counter += 1
  return `${prefix}-${Date.now().toString(36)}-${counter}`
}
