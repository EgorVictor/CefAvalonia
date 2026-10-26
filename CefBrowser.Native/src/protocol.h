// Wire protocol shared with CefSharp.Avalonia (managed side).
//
// Every message is one line:  {command-or-event}|{field}|{field}...
// Free-text fields (URLs, titles, dialog text, paths) are escaped so that '|',
// CR and LF can never break framing:
//     '\\' -> "\\\\"    '|' -> "\\p"    '\n' -> "\\n"    '\r' -> "\\r"
//
// Protocol history:
//   2 - pipe/stdio without escaping
//   3 - escaped text fields, JS dialog round-trip, Hello handshake
#pragma once

#include <string>
#include <vector>

namespace proto {

constexpr int kVersion = 3;

// Escapes a free-text field. Safe to call on already-escaped input (it is
// escaped again).
std::string Escape(const std::string& value);

// Reverses Escape(). Unknown escape sequences are kept verbatim so that a
// newer peer can still be parsed by an older one.
std::string Unescape(const std::string& value);

// Splits a payload on unescaped '|'. Fields are returned unescaped.
// |maxFields| limits how many fields are produced; the last field keeps the
// remaining separators (used for "{prefix}|{arbitrary text}" payloads).
std::vector<std::string> Split(const std::string& payload,
                               size_t maxFields = static_cast<size_t>(-1));

// Splits a leading "{id}|{rest}" payload. Returns false when the id is missing
// or not positive.
bool SplitId(const std::string& payload, int& id, std::string& rest);

} // namespace proto
