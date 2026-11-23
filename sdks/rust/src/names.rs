//! Rules shared by the bridges.

/// Identifier-like event names (the structlog mapping's rule): letters, digits, `_ . : - /`, at most 128 chars,
/// starting with a letter or `_`. Free text is a message, not an event name.
#[allow(dead_code)]
pub(crate) fn is_event_name(s: &str) -> bool {
    let b = s.as_bytes();
    !b.is_empty()
        && b.len() <= 128
        && (b[0].is_ascii_alphabetic() || b[0] == b'_')
        && b.iter().all(|&c| c.is_ascii_alphanumeric() || matches!(c, b'_' | b'.' | b':' | b'/' | b'-'))
}

/// `len` hex digits, not all zero (all-zero trace and span ids are invalid).
#[allow(dead_code)]
pub(crate) fn is_hex(s: &str, len: usize) -> bool {
    s.len() == len && s.bytes().all(|c| c.is_ascii_hexdigit()) && s.bytes().any(|c| c != b'0')
}
