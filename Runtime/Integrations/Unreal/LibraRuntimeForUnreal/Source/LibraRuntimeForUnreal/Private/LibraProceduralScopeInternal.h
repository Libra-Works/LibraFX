#pragma once
#include "LibraProceduralScope.h"
#include <map>

struct FLibraProceduralResourceDefinition
{
    liberafx::procedural_adapter::Definition Native;
    FString Textures[4];
};
struct FLibraProceduralDrawDefinition
{
    FString Model, Material;
    liberafx::procedural_adapter::Input Inputs[4];
};
struct FLibraProceduralDefinition
{
    std::map<FString, FLibraProceduralResourceDefinition> Resources;
    std::vector<FLibraProceduralDrawDefinition> Draws;
    uint64 Bytes = 0;
};
