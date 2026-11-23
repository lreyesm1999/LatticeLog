//! Contexts (specs/context-model.md): immutable snapshots of inherited attributes that form a chain to the
//! resource root. A record references its innermost context; the writer emits each context line once per file,
//! before its first use. Encoding happens once, when the context is built.

use crate::json;
use crate::value::Value;
use std::cell::RefCell;
use std::marker::PhantomData;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

static NEXT_ID: AtomicU64 = AtomicU64::new(1);

/// Kind of a context, as in the event model.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ContextKind {
    Resource,
    Scope,
    Operation,
    Request,
    Custom,
}

impl ContextKind {
    fn name(self) -> &'static str {
        match self {
            ContextKind::Resource => "Resource",
            ContextKind::Scope => "Scope",
            ContextKind::Operation => "Operation",
            ContextKind::Request => "Request",
            ContextKind::Custom => "Custom",
        }
    }
}

/// A built context. Cheap to clone (one `Arc`).
#[derive(Clone)]
pub struct Context(pub(crate) Arc<ContextInner>);

pub(crate) struct ContextInner {
    /// Process-unique identity; the writer maps it to a file-local `ref`.
    pub id: u64,
    /// `"kind":…,"name":…,"attributes":[…]` (the body of the context line).
    pub def: Box<[u8]>,
    pub parent: Option<Context>,
    /// `,"operation_id":…[,"parent_operation_id":…]` copied into every record of this context, or empty.
    pub op_fields: Box<[u8]>,
    pub op_id: Option<Box<str>>,
}

impl Context {
    /// The operation id in effect in this context, if any.
    pub fn operation_id(&self) -> Option<&str> {
        self.0.op_id.as_deref()
    }

    pub fn parent(&self) -> Option<&Context> {
        self.0.parent.as_ref()
    }

    /// Makes this the thread's current context until the guard is dropped. Records written on this thread without
    /// an explicit context use it. Not for `async` code that moves between threads: use the `tracing` layer (spans
    /// follow futures) or pass the context explicitly with `Record::context`.
    pub fn enter(&self) -> ContextGuard {
        let previous = CURRENT.with(|c| c.borrow_mut().replace(self.clone()));
        ContextGuard { previous, _not_send: PhantomData }
    }
}

impl std::fmt::Debug for Context {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Context")
            .field("id", &self.0.id)
            .field("def", &String::from_utf8_lossy(&self.0.def))
            .field("operation_id", &self.0.op_id)
            .finish()
    }
}

thread_local! {
    pub(crate) static CURRENT: RefCell<Option<Context>> = const { RefCell::new(None) };
}

/// The thread's current context (set by [`Context::enter`] / [`ContextBuilder::enter`]).
pub fn current() -> Option<Context> {
    CURRENT.with(|c| c.borrow().clone())
}

/// Restores the previous current context when dropped.
#[must_use = "the context is current only while the guard lives"]
#[derive(Debug)]
pub struct ContextGuard {
    previous: Option<Context>,
    _not_send: PhantomData<*const ()>,
}

impl Drop for ContextGuard {
    fn drop(&mut self) {
        let previous = self.previous.take();
        CURRENT.with(|c| *c.borrow_mut() = previous);
    }
}

/// Builder returned by `Logger::scope`, `Logger::operation` and `Logger::context`.
#[must_use = "call .build() or .enter()"]
#[derive(Debug)]
pub struct ContextBuilder {
    kind: ContextKind,
    parent: Option<Context>,
    name: Option<String>,
    attrs: Vec<u8>,
    op: Option<(String, Option<String>)>,
}

impl ContextBuilder {
    pub(crate) fn new(kind: ContextKind, name: Option<&str>, parent: Option<Context>) -> Self {
        ContextBuilder { kind, parent, name: name.map(str::to_owned), attrs: Vec::new(), op: None }
    }

    pub(crate) fn operation(mut self, id: String) -> Self {
        let parent_op = self.parent.as_ref().and_then(|p| p.0.op_id.as_deref()).map(str::to_owned);
        self.op = Some((id, parent_op));
        self
    }

    /// Adds an attribute (order and duplicates are kept).
    pub fn attr(mut self, key: &str, value: impl Value) -> Self {
        if !self.attrs.is_empty() {
            self.attrs.push(b',');
        }
        self.attrs.push(b'[');
        json::write_str(&mut self.attrs, key);
        self.attrs.push(b',');
        value.encode(&mut self.attrs);
        self.attrs.push(b']');
        self
    }

    /// Internal: appends pre-encoded pairs (`["k",v],["k",v]`).
    pub(crate) fn raw_attrs(mut self, pairs: &[u8]) -> Self {
        if !pairs.is_empty() {
            if !self.attrs.is_empty() {
                self.attrs.push(b',');
            }
            self.attrs.extend_from_slice(pairs);
        }
        self
    }

    /// Replaces the parent (by default the thread's current context, else the logger's resource context).
    pub fn parent(mut self, parent: Option<&Context>) -> Self {
        self.parent = parent.cloned();
        if let Some((_, p)) = &mut self.op {
            *p = self.parent.as_ref().and_then(|c| c.0.op_id.as_deref()).map(str::to_owned);
        }
        self
    }

    pub fn build(self) -> Context {
        let mut def = Vec::with_capacity(32 + self.attrs.len());
        def.extend_from_slice(b"\"kind\":\"");
        def.extend_from_slice(self.kind.name().as_bytes());
        def.push(b'"');
        if let Some(n) = &self.name {
            def.extend_from_slice(b",\"name\":");
            json::write_str(&mut def, n);
        }
        def.extend_from_slice(b",\"attributes\":[");
        def.extend_from_slice(&self.attrs);
        def.push(b']');
        let (op_fields, op_id) = match self.op {
            Some((id, parent)) => {
                let mut f = Vec::new();
                f.extend_from_slice(b",\"operation_id\":");
                json::write_str(&mut f, &id);
                if let Some(p) = parent {
                    f.extend_from_slice(b",\"parent_operation_id\":");
                    json::write_str(&mut f, &p);
                }
                (f.into_boxed_slice(), Some(id.into_boxed_str()))
            }
            None => match &self.parent {
                Some(p) => (p.0.op_fields.clone(), p.0.op_id.clone()),
                None => (Box::default(), None),
            },
        };
        Context(Arc::new(ContextInner {
            id: NEXT_ID.fetch_add(1, Ordering::Relaxed),
            def: def.into_boxed_slice(),
            parent: self.parent,
            op_fields,
            op_id,
        }))
    }

    /// Builds the context and makes it the thread's current context until the guard is dropped.
    pub fn enter(self) -> ContextGuard {
        self.build().enter()
    }
}

/// Random 64-bit operation id in hex (the Python SDK's `os.urandom(8).hex()`).
pub(crate) fn random_operation_id() -> String {
    json::hex_string(&crate::folder::random_bytes::<8>())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn operations_nest_and_scopes_inherit_the_operation() {
        let root = ContextBuilder::new(ContextKind::Resource, None, None).attr("service.name", "svc").build();
        assert_eq!(std::str::from_utf8(&root.0.def).unwrap(), r#""kind":"Resource","attributes":[["service.name","svc"]]"#);
        let op = ContextBuilder::new(ContextKind::Operation, Some("checkout"), Some(root.clone())).operation("op-1".into()).build();
        let child = ContextBuilder::new(ContextKind::Operation, Some("pay"), Some(op.clone())).operation("op-2".into()).build();
        let scope = ContextBuilder::new(ContextKind::Scope, Some("s"), Some(child.clone())).build();
        assert_eq!(std::str::from_utf8(&scope.0.op_fields).unwrap(), r#","operation_id":"op-2","parent_operation_id":"op-1""#);
        assert_eq!(scope.operation_id(), Some("op-2"));
        assert!(root.0.op_fields.is_empty());
    }

    #[test]
    fn enter_restores_the_previous_context() {
        let a = ContextBuilder::new(ContextKind::Scope, Some("a"), None).build();
        let b = ContextBuilder::new(ContextKind::Scope, Some("b"), None).build();
        {
            let _ga = a.enter();
            {
                let _gb = b.enter();
                assert_eq!(current().unwrap().0.id, b.0.id);
            }
            assert_eq!(current().unwrap().0.id, a.0.id);
        }
        assert!(current().is_none());
    }
}
