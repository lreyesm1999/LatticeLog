//! Exceptions: type, message, stack text and inner causes (specs/event-model.md §7: depth ≤ 8, ≤ 16 inner per level).

use crate::json;
use std::borrow::Cow;
use std::error::Error;

const MAX_DEPTH: usize = 8;
const MAX_INNER: usize = 16;

/// An exception attached to a record. Built from a Rust error ([`Exception::from_error`] keeps the concrete type
/// name), or by hand for errors that cross a language boundary.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Exception {
    pub type_name: Cow<'static, str>,
    pub message: Option<String>,
    pub stack: Option<String>,
    pub inner: Vec<Exception>,
}

impl Exception {
    pub fn new(type_name: impl Into<Cow<'static, str>>, message: impl Into<String>) -> Self {
        Exception { type_name: type_name.into(), message: Some(message.into()), stack: None, inner: Vec::new() }
    }

    /// The error with its real type (`std::any::type_name`), its message and its `source()` chain as inner
    /// exceptions.
    pub fn from_error<E: Error + 'static>(error: &E) -> Self {
        let mut e = Exception {
            type_name: Cow::Borrowed(std::any::type_name::<E>()),
            message: Some(error.to_string()),
            stack: None,
            inner: Vec::new(),
        };
        if let Some(src) = error.source() {
            e.inner.push(Self::from_dyn_depth(src, 1));
        }
        e
    }

    /// A type-erased error (`&dyn Error`, `anyhow::Error::as_ref()`, `tracing`'s `record_error`). Rust cannot
    /// name the concrete type of a `dyn Error`: common standard-library errors are recognized, anything else is
    /// typed `dyn std::error::Error`.
    pub fn from_dyn(error: &(dyn Error + 'static)) -> Self {
        Self::from_dyn_depth(error, 0)
    }

    fn from_dyn_depth(error: &(dyn Error + 'static), depth: usize) -> Self {
        let mut e =
            Exception { type_name: Cow::Borrowed(dyn_type_name(error)), message: Some(error.to_string()), stack: None, inner: Vec::new() };
        if depth + 1 < MAX_DEPTH
            && let Some(src) = error.source()
        {
            e.inner.push(Self::from_dyn_depth(src, depth + 1));
        }
        e
    }

    /// Stack text, kept verbatim (for example `std::backtrace::Backtrace::force_capture().to_string()`).
    pub fn with_stack(mut self, stack: impl Into<String>) -> Self {
        self.stack = Some(stack.into());
        self
    }

    pub fn with_inner(mut self, inner: Exception) -> Self {
        self.inner.push(inner);
        self
    }

    pub(crate) fn encode(&self, out: &mut Vec<u8>) {
        self.encode_depth(out, 0)
    }

    fn encode_depth(&self, out: &mut Vec<u8>, depth: usize) {
        out.extend_from_slice(b"{\"type\":");
        json::write_str(out, &self.type_name);
        if let Some(m) = &self.message {
            out.extend_from_slice(b",\"message\":");
            json::write_str(out, m);
        }
        if let Some(s) = &self.stack {
            out.extend_from_slice(b",\"stack\":");
            json::write_str(out, s);
        }
        if !self.inner.is_empty() && depth + 1 < MAX_DEPTH {
            out.extend_from_slice(b",\"inner\":[");
            for (i, x) in self.inner.iter().take(MAX_INNER).enumerate() {
                if i > 0 {
                    out.push(b',');
                }
                x.encode_depth(out, depth + 1);
            }
            out.push(b']');
        }
        out.push(b'}');
    }
}

fn dyn_type_name(e: &(dyn Error + 'static)) -> &'static str {
    macro_rules! known {
        ($($t:ty),*) => {$(
            if e.is::<$t>() { return std::any::type_name::<$t>(); }
        )*};
    }
    known!(
        std::io::Error,
        std::fmt::Error,
        std::num::ParseIntError,
        std::num::ParseFloatError,
        std::num::TryFromIntError,
        std::str::Utf8Error,
        std::string::FromUtf8Error,
        std::str::ParseBoolError,
        std::char::ParseCharError,
        std::net::AddrParseError,
        std::time::SystemTimeError,
        std::env::VarError,
        std::array::TryFromSliceError
    );
    "dyn std::error::Error"
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fmt;

    #[derive(Debug)]
    struct Payment(std::io::Error);
    impl fmt::Display for Payment {
        fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
            f.write_str("payment failed")
        }
    }
    impl Error for Payment {
        fn source(&self) -> Option<&(dyn Error + 'static)> {
            Some(&self.0)
        }
    }

    #[test]
    fn keeps_the_real_type_and_the_cause() {
        let e = Payment(std::io::Error::other("sku"));
        let x = Exception::from_error(&e);
        assert!(x.type_name.ends_with("Payment"), "{}", x.type_name);
        assert_eq!(x.inner.len(), 1);
        let io = std::any::type_name::<std::io::Error>();
        assert_eq!(x.inner[0].type_name, io);
        let mut out = Vec::new();
        x.encode(&mut out);
        let s = String::from_utf8(out).unwrap();
        assert!(s.contains(&format!(r#""message":"payment failed","inner":[{{"type":"{io}","message":"sku"}}]"#)), "{s}");
    }
}
