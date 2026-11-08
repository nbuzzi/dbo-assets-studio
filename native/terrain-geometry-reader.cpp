// Trusted extracted Taiwan 1.4 terrain only. No renderer or PathEngine calls.
// TGV1 is an intermediate vertex snapshot, NOT server navigation data.
#include "precomp_ntlpresentation.h"
#include "NtlWorldCommon.h"
#include "NtlWorldSectorInfo.h"
#include <fstream>
#include <string>
#include <vector>
#include <set>
#include <cmath>
#include <cstdio>
#include <cstdlib>

static void Require(bool ok, const char* what) {
    if (!ok) { fprintf(stderr, "FAIL %s\n", what); exit(1); }
}
static bool MaterialFields = false;
static bool MaterialParentheses = false;
static void UInt(FILE* out, unsigned value) {
    Require(fwrite(&value, sizeof(value), 1, out) == 1, "write uint32");
}
// Catalog the exact material fields used by SectorMaterialLoadFromFile without
// creating textures. Cross-check its end against the production FILE skipper.
static void Skip(FILE* f, long size, long bytes) {
    Require(bytes >= 0 && ftell(f) >= 0 && bytes <= size - ftell(f), "material read bound");
    Require(fseek(f, bytes, SEEK_CUR) == 0, "material seek");
}
static unsigned ReadUInt(FILE* f) {
    unsigned value = 0; Require(fread(&value, sizeof(value), 1, f) == 1, "material uint32"); return value;
}
static bool Flag(FILE* f) {
    unsigned value = ReadUInt(f); Require(value <= 1, "material bool32"); return value != 0;
}
static void Name(FILE* f, FILE* catalog, unsigned field, unsigned sector, unsigned layer) {
    long prefix = ftell(f); Require(prefix >= 0, "material name prefix");
    unsigned length = ReadUInt(f);
    Require(length > 0 && length < rwTEXTUREBASENAMELENGTH, "material basename length");
    char name[rwTEXTUREBASENAMELENGTH] = {};
    Require(fread(name, 1, length, f) == length, "material basename");
    for (unsigned i = 0; i < length; ++i) {
        bool literal = (name[i] >= 'a' && name[i] <= 'z') || (name[i] >= 'A' && name[i] <= 'Z') ||
            (name[i] >= '0' && name[i] <= '9') || name[i] == '_' || name[i] == '-' ||
            (MaterialParentheses && (name[i] == '(' || name[i] == ')'));
        if (!literal) {
            fprintf(stderr, "material field=%u sector=%u layer=%u prefix=%ld length=%u nameHex=", field, sector, layer, prefix, length);
            for (unsigned k = 0; k < length; ++k) fprintf(stderr, "%02X", (unsigned char)name[k]);
            fprintf(stderr, "\n");
        }
        Require(literal, "literal material basename");
    }
    if (MaterialFields)
        Require(fprintf(catalog, "%u\t%u\t%u\t%s\t%ld\t%u\n", field, sector, layer, name, prefix, length) > 0, "material field catalog write");
    else Require(fprintf(catalog, "%u\t%u\t%u\t%s\n", field, sector, layer, name) > 0, "material catalog write");
}
static void Materials(FILE* f, long size, FILE* catalog, unsigned field, unsigned sector) {
    const long matrixBytes = sizeof(D3DXMATRIX) * 2 + sizeof(RwReal) + sizeof(RwUInt8);
    Name(f, catalog, field, sector, 0); Skip(f, size, matrixBytes);
    for (unsigned layer = 0; layer < dNTL_SPLAT_LAYER_NUM; ++layer) {
        if (!Flag(f)) continue;
        Name(f, catalog, field, sector, layer + 1);
        if (Flag(f)) Skip(f, size, dGET_WORLD_PARAM()->WorldSectorTileNum * dGET_WORLD_PARAM()->WorldSectorTileNum);
        Skip(f, size, matrixBytes);
    }
}
int main(int argc, char** argv) {
    MaterialFields = argc == 5 && strcmp(argv[1], "--material-fields") == 0;
    MaterialParentheses = argc == 5 && strcmp(argv[1], "--material-parentheses") == 0;
    if (argc != 4 && !MaterialFields && !MaterialParentheses) return 2;
    if (MaterialFields || MaterialParentheses) ++argv;
    FILE* gwp = fopen(argv[1], "rb"); Require(gwp != NULL, "GWP");
    fseek(gwp, 0, SEEK_END); long gwpSize = ftell(gwp); rewind(gwp);
    LoadFileWorldState(dGET_WORLD_PARAM(), gwp);
    Require(ftell(gwp) == gwpSize && !ferror(gwp), "native complete GWP"); fclose(gwp);
    auto* p = dGET_WORLD_PARAM();
    Require(strcmp(p->WorldVer, dNTL_WORLD_VERSION) == 0, "Taiwan 1.4 only");
    memcpy(p->WorldLoadVer, p->WorldVer, 10);
    Require(p->WorldFieldNum > 0 && p->WorldFieldNum <= 64 && p->WorldFieldSectorNum == 2 &&
        p->WorldSectorVertNum > 1 && p->WorldSectorVertNum <= 129 &&
        p->WorldSectorVertNum == p->WorldSectorTileNum + 1 && p->WorldSectorTileSize > 0 &&
        p->WorldSectorSize == p->WorldSectorTileNum * p->WorldSectorTileSize &&
        p->WorldFieldSize == 2 * p->WorldSectorSize &&
        p->WorldFieldNum * p->WorldFieldSize == 2 * p->WorldSizeHalf, "consistent native dimensions");
    unsigned fields = unsigned(p->WorldFieldNum * p->WorldFieldNum);
    unsigned cross = unsigned(p->WorldSectorVertNum), count = cross * cross;
    Require(uint64_t(fields) * 4 * count * sizeof(RwV3d) <= 128ULL * 1024 * 1024, "bounded vertex snapshot");
    std::ifstream list(argv[2]); Require(bool(list), "field list");
    std::vector<std::string> paths; std::string line;
    while (std::getline(list, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        Require(!line.empty(), "literal nonempty path"); paths.push_back(line);
    }
    Require(paths.size() == fields, "complete nominal grid required");
    FILE* out = fopen(argv[3], "wbx"); Require(out != NULL, "new output");
    std::string materialPath = std::string(argv[3]) + ".materials.tsv";
    FILE* catalog = fopen(materialPath.c_str(), "wbx"); Require(catalog != NULL, "new material catalog");
    Require(fwrite("TGV1", 4, 1, out) == 1, "header");
    UInt(out, unsigned(p->WorldFieldNum)); UInt(out, 2); UInt(out, cross);
    UInt(out, unsigned(p->WorldSectorTileSize)); UInt(out, unsigned(p->WorldFieldSize));
    UInt(out, unsigned(p->WorldSizeHalf)); UInt(out, fields);
    CNtlWorldSectorInfo reader; std::vector<RwV3d> vertices(count);
    float minY = RwRealMAXVAL, maxY = -RwRealMAXVAL;
    for (unsigned field = 0; field < fields; ++field) {
        FILE* f = fopen(paths[field].c_str(), "rb"); Require(f != NULL, "geometry input");
        fseek(f, 0, SEEK_END); long size = ftell(f); rewind(f);
        for (unsigned sector = 0; sector < 4; ++sector) {
            long start = ftell(f); Materials(f, size, catalog, field, sector);
            long materialEnd = ftell(f); Require(fseek(f, start, SEEK_SET) == 0, "material rewind");
            reader.SectorMaterialSkipToFile(f);
            Require(ftell(f) == materialEnd, "catalog matches native material boundary");
            Require(ftell(f) > start && ftell(f) < size && !ferror(f) && !feof(f), "material boundary");
            const long heightStart = ftell(f), heightBytes = long(sizeof(RwV3d) + vertices.size() * sizeof(RwReal));
            Require(heightStart >= 0 && heightStart <= size && heightBytes <= size - heightStart, "complete compressed native height payload");
            Require(reader.SectorHeightfieldLoadFromFile(f, vertices.data()) != FALSE, "native heightfield");
            Require(ftell(f) == heightStart + heightBytes && !ferror(f) && !feof(f), "height boundary");
            // Production export enumerates fields and sectors X first, then Z.
            Require(vertices[0].x == -p->WorldSizeHalf + (int(field) % p->WorldFieldNum) * p->WorldFieldSize +
                (int(sector) % 2 + 1) * p->WorldSectorSize &&
                vertices[0].z == -p->WorldSizeHalf + (int(field) / p->WorldFieldNum) * p->WorldFieldSize +
                (int(sector) / 2) * p->WorldSectorSize, "sector belongs to declared field");
            for (unsigned i = 0; i < count; ++i) {
                const auto& v = vertices[i];
                Require(std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z) &&
                    fabs(v.x) <= p->WorldSizeHalf && fabs(v.z) <= p->WorldSizeHalf, "finite bounded world position");
                Require(v.x == vertices[0].x - (i % cross) * p->WorldSectorTileSize &&
                    v.z == vertices[0].z + (i / cross) * p->WorldSectorTileSize, "native vertex lattice");
                if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y;
            }
            UInt(out, field); UInt(out, sector); UInt(out, count);
            Require(fwrite(vertices.data(), sizeof(RwV3d), count, out) == count, "exact native vertices");
            fseek(f, start, SEEK_SET); reader.OutdoorSectorSkipToFile(f);
            Require(ftell(f) > start && ftell(f) <= size && !ferror(f) && !feof(f), "complete sector boundary");
        }
        Require(ftell(f) == size, "complete field EOF"); fclose(f);
    }
    Require(fclose(out) == 0, "flush snapshot");
    Require(fclose(catalog) == 0, "flush material catalog");
    printf("PASS fields=%u sectors=%u vertices=%u minY=%.9g maxY=%.9g; terrain only, NOT navigation\n",
        fields, fields * 4, fields * 4 * count, minY, maxY);
    return 0;
}
