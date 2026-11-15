#include <SKSE/SKSE.h>

using namespace std::literals;

SKSEPluginInfo(
    .Version = REL::Version{ 0, 1, 0, 0 },
    .Name = "Drone Swarm Skyrim"sv,
    .Author = "Drone Swarm"sv,
    .SupportEmail = ""sv,
    .StructCompatibility = SKSE::StructCompatibility::Independent,
    .RuntimeCompatibility = SKSE::VersionIndependence::AddressLibrary,
    .MinimumSKSEVersion = REL::Version{ 2, 2, 6, 0 }
)
