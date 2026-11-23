//! Typed attribute values. Every type keeps its identity on the wire (specs/jsonl-exchange.md): `1` stays int64,
//! `1.0` stays float64, NaN keeps its bits, `u64` beyond `i64::MAX` becomes UInt64, and the wrappers below carry
//! bytes, timestamps, durations, UUIDs, decimals (with their scale), ordered maps and raw UTF-16.

use crate::json;
use std::borrow::Cow;
use std::collections::{BTreeMap, HashMap};
use std::fmt;
use std::rc::Rc;
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// A value that can be written losslessly into a record. Implemented for the standard scalar types, strings,
/// `Option`, slices, `Vec`, maps, [`SystemTime`], [`Duration`] and the wrappers of this module. Object safe:
/// `&[&dyn Value]` is a heterogeneous array.
pub trait Value {
    /// Appends the JSON form of the value.
    fn encode(&self, out: &mut Vec<u8>);
}

impl<T: Value + ?Sized> Value for &T {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        (**self).encode(out)
    }
}

impl<T: Value + ?Sized> Value for &mut T {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        (**self).encode(out)
    }
}

impl<T: Value + ?Sized> Value for Box<T> {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        (**self).encode(out)
    }
}

impl<T: Value + ?Sized> Value for Arc<T> {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        (**self).encode(out)
    }
}

impl<T: Value + ?Sized> Value for Rc<T> {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        (**self).encode(out)
    }
}

impl Value for bool {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        out.extend_from_slice(if *self { b"true" } else { b"false" });
    }
}

macro_rules! signed {
    ($($t:ty),*) => {$(
        impl Value for $t {
            #[inline]
            fn encode(&self, out: &mut Vec<u8>) { json::write_i64(out, *self as i64) }
        }
    )*};
}
signed!(i8, i16, i32, i64, isize);

macro_rules! unsigned {
    ($($t:ty),*) => {$(
        impl Value for $t {
            #[inline]
            fn encode(&self, out: &mut Vec<u8>) { json::write_unsigned(out, *self as u64) }
        }
    )*};
}
unsigned!(u8, u16, u32, u64, usize);

impl Value for i128 {
    fn encode(&self, out: &mut Vec<u8>) {
        if let Ok(v) = i64::try_from(*self) {
            json::write_i64(out, v)
        } else if let Ok(v) = u64::try_from(*self) {
            json::write_unsigned(out, v)
        } else {
            out.extend_from_slice(b"{\"$dec\":\"");
            out.extend_from_slice(itoa::Buffer::new().format(*self).as_bytes());
            out.extend_from_slice(b"\"}");
        }
    }
}

impl Value for u128 {
    fn encode(&self, out: &mut Vec<u8>) {
        if let Ok(v) = u64::try_from(*self) {
            json::write_unsigned(out, v)
        } else {
            out.extend_from_slice(b"{\"$dec\":\"");
            out.extend_from_slice(itoa::Buffer::new().format(*self).as_bytes());
            out.extend_from_slice(b"\"}");
        }
    }
}

impl Value for f64 {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        json::write_f64(out, *self)
    }
}

impl Value for f32 {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        json::write_f64(out, f64::from(*self))
    }
}

impl Value for str {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        json::write_str(out, self)
    }
}

impl Value for String {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        json::write_str(out, self)
    }
}

impl Value for Cow<'_, str> {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        json::write_str(out, self)
    }
}

impl Value for char {
    fn encode(&self, out: &mut Vec<u8>) {
        json::write_str(out, self.encode_utf8(&mut [0; 4]))
    }
}

impl Value for fmt::Arguments<'_> {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        json::write_args(out, *self)
    }
}

impl Value for () {
    fn encode(&self, out: &mut Vec<u8>) {
        out.extend_from_slice(b"null");
    }
}

impl<T: Value> Value for Option<T> {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        match self {
            Some(v) => v.encode(out),
            None => out.extend_from_slice(b"null"),
        }
    }
}

impl<T: Value> Value for [T] {
    fn encode(&self, out: &mut Vec<u8>) {
        out.push(b'[');
        for (i, v) in self.iter().enumerate() {
            if i > 0 {
                out.push(b',');
            }
            v.encode(out);
        }
        out.push(b']');
    }
}

impl<T: Value, const N: usize> Value for [T; N] {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        self.as_slice().encode(out)
    }
}

impl<T: Value> Value for Vec<T> {
    #[inline]
    fn encode(&self, out: &mut Vec<u8>) {
        self.as_slice().encode(out)
    }
}

fn encode_map<'a, K: AsRef<str> + 'a, V: Value + 'a>(out: &mut Vec<u8>, entries: impl Iterator<Item = (&'a K, &'a V)>) {
    out.extend_from_slice(b"{\"$map\":[");
    for (i, (k, v)) in entries.enumerate() {
        if i > 0 {
            out.push(b',');
        }
        out.push(b'[');
        json::write_str(out, k.as_ref());
        out.push(b',');
        v.encode(out);
        out.push(b']');
    }
    out.extend_from_slice(b"]}");
}

impl<K: AsRef<str>, V: Value> Value for BTreeMap<K, V> {
    fn encode(&self, out: &mut Vec<u8>) {
        encode_map(out, self.iter())
    }
}

impl<K: AsRef<str>, V: Value, S> Value for HashMap<K, V, S> {
    fn encode(&self, out: &mut Vec<u8>) {
        encode_map(out, self.iter())
    }
}

impl Value for SystemTime {
    fn encode(&self, out: &mut Vec<u8>) {
        Timestamp::from(*self).encode(out)
    }
}

impl Value for Duration {
    fn encode(&self, out: &mut Vec<u8>) {
        out.extend_from_slice(b"{\"$dur_ns\":\"");
        out.extend_from_slice(itoa::Buffer::new().format(self.as_nanos()).as_bytes());
        out.extend_from_slice(b"\"}");
    }
}

/// Raw bytes (`{"$bytes":"<base64>"}`). `Vec<u8>` alone is an array of numbers.
#[derive(Clone, Copy, Debug)]
pub struct Bytes<'a>(pub &'a [u8]);

impl Value for Bytes<'_> {
    fn encode(&self, out: &mut Vec<u8>) {
        out.extend_from_slice(b"{\"$bytes\":\"");
        json::write_base64(out, self.0);
        out.extend_from_slice(b"\"}");
    }
}

/// A point in time as Unix nanoseconds (UTC), written as `{"$ts":"…"}` with 9 fractional digits.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct Timestamp(pub i64);

impl Timestamp {
    /// The current wall-clock time.
    #[inline]
    pub fn now() -> Self {
        Self::from(SystemTime::now())
    }
}

impl From<SystemTime> for Timestamp {
    fn from(t: SystemTime) -> Self {
        match t.duration_since(UNIX_EPOCH) {
            Ok(d) => Timestamp(i64::try_from(d.as_nanos()).unwrap_or(i64::MAX)),
            Err(e) => Timestamp(i64::try_from(e.duration().as_nanos()).map_or(i64::MIN, |n| -n)),
        }
    }
}

impl Value for Timestamp {
    fn encode(&self, out: &mut Vec<u8>) {
        out.extend_from_slice(b"{\"$ts\":");
        json::write_ts(out, self.0);
        out.push(b'}');
    }
}

/// A UUID (`{"$uuid":"…"}`), from its 16 bytes in RFC 4122 order or a `u128`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct Uuid(pub [u8; 16]);

impl Uuid {
    pub const fn from_u128(v: u128) -> Self {
        Uuid(v.to_be_bytes())
    }
}

impl Value for Uuid {
    fn encode(&self, out: &mut Vec<u8>) {
        out.extend_from_slice(b"{\"$uuid\":\"");
        let b = &self.0;
        json::write_hex(out, &b[0..4]);
        out.push(b'-');
        json::write_hex(out, &b[4..6]);
        out.push(b'-');
        json::write_hex(out, &b[6..8]);
        out.push(b'-');
        json::write_hex(out, &b[8..10]);
        out.push(b'-');
        json::write_hex(out, &b[10..16]);
        out.extend_from_slice(b"\"}");
    }
}

/// A decimal number in its text form (`"19.990"`), scale preserved: `{"$dec":"19.990"}`. The text is passed
/// through as given; LatticeLog stores up to 28 fractional digits and a 96-bit mantissa.
#[derive(Clone, Copy, Debug)]
pub struct Decimal<'a>(pub &'a str);

impl Value for Decimal<'_> {
    fn encode(&self, out: &mut Vec<u8>) {
        out.extend_from_slice(b"{\"$dec\":");
        json::write_str(out, self.0);
        out.push(b'}');
    }
}

/// An ordered map with heterogeneous values; duplicates are kept: `Map(&[("a", &1), ("b", &"x")])`.
#[derive(Clone, Copy)]
pub struct Map<'a>(pub &'a [(&'a str, &'a dyn Value)]);

impl fmt::Debug for Map<'_> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let mut out = Vec::new();
        self.encode(&mut out);
        f.write_str(&String::from_utf8_lossy(&out))
    }
}

impl Value for Map<'_> {
    fn encode(&self, out: &mut Vec<u8>) {
        encode_map(out, self.0.iter().map(|(k, v)| (k, v)))
    }
}

/// UTF-16 code units, exactly. Well-formed text is written as a normal string; text with unpaired surrogates
/// (from Windows APIs or JavaScript) travels as `{"$utf16le":"<base64>"}` and is preserved bit for bit.
#[derive(Clone, Copy, Debug)]
pub struct Utf16<'a>(pub &'a [u16]);

impl Value for Utf16<'_> {
    fn encode(&self, out: &mut Vec<u8>) {
        match String::from_utf16(self.0) {
            Ok(s) => json::write_str(out, &s),
            Err(_) => {
                out.extend_from_slice(b"{\"$utf16le\":\"");
                let bytes: Vec<u8> = self.0.iter().flat_map(|u| u.to_le_bytes()).collect();
                json::write_base64(out, &bytes);
                out.extend_from_slice(b"\"}");
            }
        }
    }
}

/// Any `Display` value, written as a string without a temporary `String`: `.attr("addr", Display(&addr))`.
#[derive(Clone, Copy, Debug)]
pub struct Display<T>(pub T);

impl<T: fmt::Display> Value for Display<T> {
    fn encode(&self, out: &mut Vec<u8>) {
        json::write_display(out, &self.0)
    }
}

/// Any `Debug` value, written as a string: `.attr("req", Debug(&req))`.
#[derive(Clone, Copy)]
pub struct Debug<T>(pub T);

impl<T: fmt::Debug> fmt::Debug for Debug<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        self.0.fmt(f)
    }
}

impl<T: fmt::Debug> Value for Debug<T> {
    fn encode(&self, out: &mut Vec<u8>) {
        out.push(b'"');
        let _ = fmt::write(&mut json::EscapeWriter(out), format_args!("{:?}", self.0));
        out.push(b'"');
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn enc(v: &dyn Value) -> String {
        let mut out = Vec::new();
        v.encode(&mut out);
        String::from_utf8(out).unwrap()
    }

    #[test]
    fn types_keep_their_identity() {
        assert_eq!(enc(&1i32), "1");
        assert_eq!(enc(&1.0f64), "1.0");
        assert_eq!(enc(&(u64::MAX)), r#"{"$u64":"18446744073709551615"}"#);
        assert_eq!(enc(&(i128::MAX)), r#"{"$dec":"170141183460469231731687303715884105727"}"#);
        assert_eq!(enc(&Bytes(&[0, 0xFF])), r#"{"$bytes":"AP8="}"#);
        assert_eq!(enc(&Timestamp(1_767_323_045_123_456_000)), r#"{"$ts":"2026-01-02T03:04:05.123456000Z"}"#);
        assert_eq!(enc(&Duration::from_millis(1500)), r#"{"$dur_ns":"1500000000"}"#);
        assert_eq!(enc(&Uuid::from_u128(255)), r#"{"$uuid":"00000000-0000-0000-0000-0000000000ff"}"#);
        assert_eq!(enc(&Decimal("19.990")), r#"{"$dec":"19.990"}"#);
        assert_eq!(enc(&Option::<i32>::None), "null");
        assert_eq!(enc(&vec![1, 2]), "[1,2]");
        let mixed: [&dyn Value; 3] = [&1, &2.5, &"x"];
        assert_eq!(enc(&Map(&[("a", &mixed)])), r#"{"$map":[["a",[1,2.5,"x"]]]}"#);
        assert_eq!(enc(&Utf16(&[0x61, 0xD800, 0x62])), r#"{"$utf16le":"YQAA2GIA"}"#);
        assert_eq!(enc(&Utf16(&[0x61, 0x62])), r#""ab""#);
        assert_eq!(enc(&Display(std::net::Ipv4Addr::LOCALHOST)), r#""127.0.0.1""#);
        assert_eq!(enc(&format_args!("n={}", 5)), r#""n=5""#);
    }

    #[test]
    fn times_before_1970_are_negative() {
        // 100 ns: the resolution of SystemTime on Windows.
        let t = UNIX_EPOCH - Duration::from_nanos(100);
        assert_eq!(Timestamp::from(t), Timestamp(-100));
    }
}
