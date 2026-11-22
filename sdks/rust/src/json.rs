//! Low-level writers for `latticelog.jsonl` v1 (specs/jsonl-exchange.md). Everything appends to a `Vec<u8>` and
//! allocates nothing beyond the vector's own growth, so a reused buffer makes encoding allocation-free.

use std::cell::Cell;
use std::fmt;

/// Escape table: 0 = copy as is, otherwise the character after the backslash (`u` = `\u00XX`).
static ESCAPE: [u8; 256] = {
    let mut t = [0u8; 256];
    let mut i = 0;
    while i < 0x20 {
        t[i] = b'u';
        i += 1;
    }
    t[b'\n' as usize] = b'n';
    t[b'\r' as usize] = b'r';
    t[b'\t' as usize] = b't';
    t[0x08] = b'b';
    t[0x0C] = b'f';
    t[b'"' as usize] = b'"';
    t[b'\\' as usize] = b'\\';
    t
};

const LO: u64 = 0x0101_0101_0101_0101;
const HI: u64 = 0x8080_8080_8080_8080;

/// True when any of the 8 bytes needs escaping (< 0x20, `"` or `\`). Exact, no false negatives.
#[inline(always)]
fn needs_escape(w: u64) -> bool {
    let below = w.wrapping_sub(LO * 0x20) & !w & HI;
    let q = w ^ (LO * b'"' as u64);
    let quote = q.wrapping_sub(LO) & !q & HI;
    let b = w ^ (LO * b'\\' as u64);
    let backslash = b.wrapping_sub(LO) & !b & HI;
    (below | quote | backslash) != 0
}

/// Appends the escaped content of `s` (no surrounding quotes). UTF-8 is copied as is (valid JSON).
#[inline]
pub fn escape_into(out: &mut Vec<u8>, s: &str) {
    let b = s.as_bytes();
    let mut start = 0;
    let mut i = 0;
    while i < b.len() {
        if i + 8 <= b.len() {
            let w = u64::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3], b[i + 4], b[i + 5], b[i + 6], b[i + 7]]);
            if !needs_escape(w) {
                i += 8;
                continue;
            }
        }
        let c = b[i];
        let e = ESCAPE[c as usize];
        if e == 0 {
            i += 1;
            continue;
        }
        out.extend_from_slice(&b[start..i]);
        if e == b'u' {
            const HEX: &[u8; 16] = b"0123456789abcdef";
            out.extend_from_slice(&[b'\\', b'u', b'0', b'0', HEX[(c >> 4) as usize], HEX[(c & 15) as usize]]);
        } else {
            out.extend_from_slice(&[b'\\', e]);
        }
        i += 1;
        start = i;
    }
    out.extend_from_slice(&b[start..]);
}

/// Appends `s` as a JSON string.
#[inline]
pub fn write_str(out: &mut Vec<u8>, s: &str) {
    out.reserve(s.len() + 2);
    out.push(b'"');
    escape_into(out, s);
    out.push(b'"');
}

/// `fmt::Write` adapter that escapes into an open JSON string: `Display`/`Debug` output is written without a
/// temporary `String`.
pub struct EscapeWriter<'a>(pub &'a mut Vec<u8>);

impl fmt::Write for EscapeWriter<'_> {
    #[inline]
    fn write_str(&mut self, s: &str) -> fmt::Result {
        escape_into(self.0, s);
        Ok(())
    }
}

/// Appends `value` formatted with `Display` as a JSON string.
#[inline]
pub fn write_display(out: &mut Vec<u8>, value: &dyn fmt::Display) {
    out.push(b'"');
    let _ = fmt::write(&mut EscapeWriter(out), format_args!("{value}"));
    out.push(b'"');
}

/// Appends `args` (from `format_args!`) as a JSON string; a literal without arguments is escaped directly.
#[inline]
pub fn write_args(out: &mut Vec<u8>, args: fmt::Arguments<'_>) {
    match args.as_str() {
        Some(s) => write_str(out, s),
        None => {
            out.push(b'"');
            let _ = fmt::write(&mut EscapeWriter(out), args);
            out.push(b'"');
        }
    }
}

#[inline]
pub fn write_i64(out: &mut Vec<u8>, v: i64) {
    out.extend_from_slice(itoa::Buffer::new().format(v).as_bytes());
}

#[inline]
pub fn write_u64(out: &mut Vec<u8>, v: u64) {
    out.extend_from_slice(itoa::Buffer::new().format(v).as_bytes());
}

/// Unsigned integer: int64 when it fits, otherwise `{"$u64":"…"}` (the same rule as the Python and PHP SDKs).
#[inline]
pub fn write_unsigned(out: &mut Vec<u8>, v: u64) {
    if v <= i64::MAX as u64 {
        write_u64(out, v);
    } else {
        out.extend_from_slice(b"{\"$u64\":\"");
        write_u64(out, v);
        out.extend_from_slice(b"\"}");
    }
}

/// float64: always with a `.` or an exponent (`1.0`, `-0.0`, `1e20`); NaN keeps its bits, infinities are named.
#[inline]
pub fn write_f64(out: &mut Vec<u8>, v: f64) {
    if v.is_finite() {
        out.extend_from_slice(ryu::Buffer::new().format_finite(v).as_bytes());
    } else if v.is_nan() {
        out.extend_from_slice(b"{\"$f64bits\":\"");
        write_hex_upper_u64(out, v.to_bits());
        out.extend_from_slice(b"\"}");
    } else if v > 0.0 {
        out.extend_from_slice(b"{\"$f64\":\"Infinity\"}");
    } else {
        out.extend_from_slice(b"{\"$f64\":\"-Infinity\"}");
    }
}

fn write_hex_upper_u64(out: &mut Vec<u8>, v: u64) {
    const HEX: &[u8; 16] = b"0123456789ABCDEF";
    for i in (0..16).rev() {
        out.push(HEX[((v >> (i * 4)) & 15) as usize]);
    }
}

/// Lower-case hex of `bytes`.
pub fn write_hex(out: &mut Vec<u8>, bytes: &[u8]) {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    for &b in bytes {
        out.push(HEX[(b >> 4) as usize]);
        out.push(HEX[(b & 15) as usize]);
    }
}

pub fn hex_string(bytes: &[u8]) -> String {
    let mut v = Vec::with_capacity(bytes.len() * 2);
    write_hex(&mut v, bytes);
    String::from_utf8(v).expect("hex is ASCII")
}

/// Standard base64 with padding.
pub fn write_base64(out: &mut Vec<u8>, data: &[u8]) {
    const T: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    out.reserve(data.len().div_ceil(3) * 4);
    let (chunks, rest) = data.as_chunks::<3>();
    for &[a, b, c] in chunks {
        let n = (u32::from(a) << 16) | (u32::from(b) << 8) | u32::from(c);
        out.extend_from_slice(&[T[(n >> 18) as usize & 63], T[(n >> 12) as usize & 63], T[(n >> 6) as usize & 63], T[n as usize & 63]]);
    }
    match *rest {
        [a] => {
            let n = u32::from(a) << 16;
            out.extend_from_slice(&[T[(n >> 18) as usize & 63], T[(n >> 12) as usize & 63], b'=', b'=']);
        }
        [a, b] => {
            let n = (u32::from(a) << 16) | (u32::from(b) << 8);
            out.extend_from_slice(&[T[(n >> 18) as usize & 63], T[(n >> 12) as usize & 63], T[(n >> 6) as usize & 63], b'=']);
        }
        _ => {}
    }
}

/// Days since 1970-01-01 -> (year, month, day). Howard Hinnant's `civil_from_days`.
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

fn two(out: &mut [u8], v: u32) {
    out[0] = b'0' + (v / 10) as u8;
    out[1] = b'0' + (v % 10) as u8;
}

fn second_prefix(secs: i64) -> [u8; 19] {
    let (y, m, d) = civil_from_days(secs.div_euclid(86_400));
    let sod = secs.rem_euclid(86_400) as u32;
    let mut p = *b"0000-00-00T00:00:00";
    let y = y.clamp(0, 9999) as u32;
    two(&mut p[0..2], y / 100);
    two(&mut p[2..4], y % 100);
    two(&mut p[5..7], m);
    two(&mut p[8..10], d);
    two(&mut p[11..13], sod / 3600);
    two(&mut p[14..16], sod / 60 % 60);
    two(&mut p[17..19], sod % 60);
    p
}

thread_local! {
    /// Last formatted second: records written in the same second reuse the date/time prefix.
    static LAST_SECOND: Cell<(i64, [u8; 19])> = const { Cell::new((i64::MIN, [0; 19])) };
}

/// Appends a JSON string with RFC 3339 UTC time and 9 fractional digits.
pub fn write_ts(out: &mut Vec<u8>, unix_nanos: i64) {
    let secs = unix_nanos.div_euclid(1_000_000_000);
    let frac = unix_nanos.rem_euclid(1_000_000_000) as u32;
    let prefix = LAST_SECOND.with(|c| {
        let (s, p) = c.get();
        if s == secs {
            p
        } else {
            let p = second_prefix(secs);
            c.set((secs, p));
            p
        }
    });
    let mut buf = [0u8; 32];
    buf[0] = b'"';
    buf[1..20].copy_from_slice(&prefix);
    buf[20] = b'.';
    let mut f = frac;
    for i in (21..30).rev() {
        buf[i] = b'0' + (f % 10) as u8;
        f /= 10;
    }
    buf[30] = b'Z';
    buf[31] = b'"';
    out.extend_from_slice(&buf);
}

/// RFC 3339 time with seconds only (`2026-11-22T10:00:00Z`), for manifests.
pub fn rfc3339_seconds(unix_secs: i64) -> String {
    let p = second_prefix(unix_secs);
    let mut s = String::from_utf8(p.to_vec()).expect("ASCII");
    s.push('Z');
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    fn s(f: impl FnOnce(&mut Vec<u8>)) -> String {
        let mut v = Vec::new();
        f(&mut v);
        String::from_utf8(v).unwrap()
    }

    #[test]
    fn escapes_exactly_what_json_needs() {
        assert_eq!(s(|o| write_str(o, "plain text, long enough for SWAR")), "\"plain text, long enough for SWAR\"");
        assert_eq!(s(|o| write_str(o, "a\"b\\c\nd\u{1}e\u{1f}")), r#""a\"b\\c\nd\u0001e\u001f""#);
        assert_eq!(s(|o| write_str(o, "héllo 世界 🎉")), "\"héllo 世界 🎉\"");
        // Every position inside and across 8-byte chunks.
        for pos in 0..20 {
            let mut text = "x".repeat(20);
            text.replace_range(pos..=pos, "\"");
            let got = s(|o| write_str(o, &text));
            assert_eq!(got, format!("\"{}\"", text.replace('"', "\\\"")));
        }
        for c in 0u8..=255 {
            let w = u64::from_le_bytes([b'a', b'a', b'a', c, b'a', b'a', b'a', b'a']);
            assert_eq!(needs_escape(w), c < 0x20 || c == b'"' || c == b'\\', "byte {c}");
        }
    }

    #[test]
    fn floats_always_look_like_floats() {
        assert_eq!(s(|o| write_f64(o, 1.0)), "1.0");
        assert_eq!(s(|o| write_f64(o, -0.0)), "-0.0");
        assert_eq!(s(|o| write_f64(o, 1e20)), "1e20");
        assert_eq!(s(|o| write_f64(o, 19.99)), "19.99");
        assert_eq!(s(|o| write_f64(o, f64::INFINITY)), r#"{"$f64":"Infinity"}"#);
        assert_eq!(s(|o| write_f64(o, f64::NEG_INFINITY)), r#"{"$f64":"-Infinity"}"#);
        assert_eq!(s(|o| write_f64(o, f64::from_bits(0x7FF8_0000_0000_0001))), r#"{"$f64bits":"7FF8000000000001"}"#);
    }

    #[test]
    fn unsigned_values_keep_their_range() {
        assert_eq!(s(|o| write_unsigned(o, 5)), "5");
        assert_eq!(s(|o| write_unsigned(o, 1 << 63)), r#"{"$u64":"9223372036854775808"}"#);
    }

    #[test]
    fn timestamps_have_nine_digits() {
        assert_eq!(s(|o| write_ts(o, 1_767_323_045_123_456_000)), "\"2026-01-02T03:04:05.123456000Z\"");
        assert_eq!(s(|o| write_ts(o, 0)), "\"1970-01-01T00:00:00.000000000Z\"");
        assert_eq!(s(|o| write_ts(o, -1)), "\"1969-12-31T23:59:59.999999999Z\"");
        assert_eq!(s(|o| write_ts(o, 951_782_400_000_000_007)), "\"2000-02-29T00:00:00.000000007Z\"");
        // Same second twice: the cached prefix must not leak into another second.
        assert_eq!(s(|o| write_ts(o, 951_782_400_500_000_000)), "\"2000-02-29T00:00:00.500000000Z\"");
        assert_eq!(s(|o| write_ts(o, 951_868_800_000_000_000)), "\"2000-03-01T00:00:00.000000000Z\"");
    }

    #[test]
    fn base64_matches_the_standard() {
        assert_eq!(s(|o| write_base64(o, b"")), "");
        assert_eq!(s(|o| write_base64(o, b"f")), "Zg==");
        assert_eq!(s(|o| write_base64(o, b"fo")), "Zm8=");
        assert_eq!(s(|o| write_base64(o, b"foo")), "Zm9v");
        assert_eq!(s(|o| write_base64(o, &[0x00, 0xFF])), "AP8=");
    }
}
