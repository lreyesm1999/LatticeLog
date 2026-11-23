// Contexts (specs/context-model.md): immutable snapshots of inherited attributes that form a chain to the resource
// root. A record references its innermost context; the writer emits each context line once per file, before its first
// use. Contexts follow async code through AsyncLocalStorage: `ctx.run(fn)` makes `ctx` current for everything `fn`
// does, including callbacks and awaits.

import { AsyncLocalStorage } from "node:async_hooks";
import { randomBytes } from "node:crypto";
import { type Attributes, encodePairs, encodeString } from "./values.js";

export type ContextKind = "Resource" | "Scope" | "Operation" | "Request" | "Custom";

/** The current context of the running async flow. Shared by every logger of the process. */
export const contextStorage = new AsyncLocalStorage<Context>();

let nextId = 1;

/** A built context. Records written while it is current (or with `{ context }`) reference it. */
export class Context {
  /** @internal Process-unique identity; the writer maps it to a file-local `ref`. */
  readonly id: number;
  readonly kind: ContextKind;
  readonly name: string | undefined;
  readonly parent: Context | undefined;
  /** The operation id in effect in this context, if any. */
  readonly operationId: string | undefined;
  /** @internal `"kind":…,"name":…,"attributes":[…]`, the body of the context line. */
  readonly def: string;
  /** @internal `,"operation_id":…[,"parent_operation_id":…]` copied into every record of this context, or "". */
  readonly opFields: string;

  /** @internal */
  constructor(kind: ContextKind, name: string | undefined, parent: Context | undefined, attributes?: Attributes, operation?: string) {
    this.id = nextId++;
    this.kind = kind;
    this.name = name;
    this.parent = parent;
    let def = '"kind":"' + kind + '"';
    if (name !== undefined) def += ',"name":' + encodeString(name);
    let pairs = "";
    if (operation !== undefined && name !== undefined) pairs = '["operation.name",' + encodeString(name) + "]";
    if (attributes) {
      const more = encodePairs(attributes);
      if (more) pairs += (pairs ? "," : "") + more;
    }
    this.def = def + ',"attributes":[' + pairs + "]";
    if (operation !== undefined) {
      this.operationId = operation;
      let f = ',"operation_id":' + encodeString(operation);
      if (parent?.operationId !== undefined) f += ',"parent_operation_id":' + encodeString(parent.operationId);
      this.opFields = f;
    } else {
      this.operationId = parent?.operationId;
      this.opFields = parent?.opFields ?? "";
    }
  }

  /** Runs `fn` with this context current (also in every callback and `await` inside it) and returns its result. */
  run<T>(fn: () => T): T {
    return contextStorage.run(this, fn);
  }

  /**
   * Makes this context current for the rest of the synchronous execution and the async work it starts
   * (`AsyncLocalStorage.enterWith`). Prefer `run`, which also restores the previous context.
   */
  enter(): void {
    contextStorage.enterWith(this);
  }
}

/** The context of the running async flow, if any. */
export function currentContext(): Context | undefined {
  return contextStorage.getStore();
}

/** Random 64-bit operation id in hex (like the Python and Rust SDKs). */
export function randomOperationId(): string {
  return randomBytes(8).toString("hex");
}
