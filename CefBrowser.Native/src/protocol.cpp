#include "protocol.h"

#include <cstdlib>

namespace proto {

std::string Escape(const std::string& value)
{
    std::string out;
    out.reserve(value.size() + 8);

    for (char c : value) {
        switch (c) {
        case '\\': out += "\\\\"; break;
        case '|':  out += "\\p";  break;
        case '\n': out += "\\n";  break;
        case '\r': out += "\\r";  break;
        default:   out.push_back(c); break;
        }
    }

    return out;
}

std::string Unescape(const std::string& value)
{
    std::string out;
    out.reserve(value.size());

    for (size_t i = 0; i < value.size(); ++i) {
        char c = value[i];
        if (c != '\\' || i + 1 >= value.size()) {
            out.push_back(c);
            continue;
        }

        char n = value[++i];
        switch (n) {
        case '\\': out.push_back('\\'); break;
        case 'p':  out.push_back('|');  break;
        case 'n':  out.push_back('\n'); break;
        case 'r':  out.push_back('\r'); break;
        default:
            out.push_back('\\');
            out.push_back(n);
            break;
        }
    }

    return out;
}

std::vector<std::string> Split(const std::string& payload, size_t maxFields)
{
    std::vector<std::string> fields;
    if (maxFields == 0)
        return fields;

    std::string current;
    current.reserve(payload.size());

    for (size_t i = 0; i < payload.size(); ++i) {
        char c = payload[i];

        // Preserve escape pairs; Unescape() resolves them below.
        if (c == '\\' && i + 1 < payload.size()) {
            current.push_back(c);
            current.push_back(payload[++i]);
            continue;
        }

        if (c == '|' && fields.size() + 1 < maxFields) {
            fields.push_back(Unescape(current));
            current.clear();
            continue;
        }

        current.push_back(c);
    }

    fields.push_back(Unescape(current));
    return fields;
}

bool SplitId(const std::string& payload, int& id, std::string& rest)
{
    size_t sep = payload.find('|');
    if (sep == std::string::npos)
        return false;

    id = atoi(payload.substr(0, sep).c_str());
    if (id <= 0)
        return false;

    rest = payload.substr(sep + 1);
    return true;
}

} // namespace proto
