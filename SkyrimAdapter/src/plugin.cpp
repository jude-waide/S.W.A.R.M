#include <SKSE/SKSE.h>
#include <RE/P/PlayerCharacter.h>
#include <RE/T/TES.h>
#include <RE/T/TESDataHandler.h>
#include <RE/T/TESObjectCELL.h>
#include <RE/T/TESWorldSpace.h>

#include <Windows.h>
#include <objbase.h>
#include <winhttp.h>

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <deque>
#include <filesystem>
#include <iomanip>
#include <memory>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <sstream>
#include <string>
#include <thread>
#include <unordered_set>
#include <utility>
#include <vector>

#include <spdlog/sinks/basic_file_sink.h>

namespace
{
	using json = nlohmann::json;
	using Clock = std::chrono::steady_clock;

	constexpr float kCellSize = 32.0F;
	constexpr int kHorizontalCells = 128;
	constexpr int kVerticalCells = 64;
	constexpr std::size_t kDroneCount = 5;

	struct WorldPoint
	{
		float x{};
		float y{};
		float z{};
	};

	struct CellPoint
	{
		int x{};
		int y{};
		int z{};
	};

	struct TerminalResult
	{
		std::string commandId;
		std::string status;
		std::string reason;
	};

	struct FlightCommand
	{
		std::string commandId;
		CellPoint destination;
		std::optional<std::string> afterCommandId;
	};

	struct Drone
	{
		std::string id;
		RE::NiPointer<RE::TESObjectREFR> reference;
		std::optional<FlightCommand> currentCommand;
		std::optional<FlightCommand> successor;
		std::deque<TerminalResult> terminalResults;
		std::unordered_set<std::string> completedCommandIds;
	};

	enum class RequestKind
	{
		Status,
		Initialize,
		Observation,
		Telemetry
	};

	struct Request
	{
		RequestKind kind{};
		std::wstring path;
		std::string body;
	};

	struct Response
	{
		RequestKind kind{};
		bool success{};
		DWORD statusCode{};
		std::string body;
		std::string error;
	};

	struct Configuration
	{
		std::wstring baseUrl{ L"http://127.0.0.1:5000" };
		std::string pluginKey;
		CellPoint target{ 40, 0, 40 };
	};

	struct MissionState
	{
		WorldPoint startPosition;
		CellPoint boundsMin;
		CellPoint tower;
		CellPoint target;
		std::string missionId;
		std::string worldId;
		std::vector<Drone> drones;
		std::uint64_t sequence{};
		Clock::time_point startedAt{};
		Clock::time_point nextFrameAt{};
		Clock::time_point nextObservationAt{};
		bool worldPrepared{};
		bool initialized{};
		bool stopped{};
	};

	Configuration g_config;
	MissionState g_mission;
	std::atomic_bool g_requestInFlight{ false };
	std::atomic_bool g_networkStopping{ false };
	std::mutex g_queueMutex;
	std::condition_variable_any g_queueChanged;
	std::deque<Request> g_requests;
	std::deque<Response> g_responses;
	std::jthread g_networkThread;
	bool g_gameReady{};
	bool g_pumpScheduled{};
	Clock::time_point g_lastMovementUpdate{};

	std::wstring GetIniPath()
	{
		std::array<wchar_t, MAX_PATH> modulePath{};
		const auto length = GetModuleFileNameW(nullptr, modulePath.data(), static_cast<DWORD>(modulePath.size()));
		if (length == 0 || length >= modulePath.size()) {
			throw std::runtime_error("Unable to resolve the Skyrim executable path.");
		}
		return (std::filesystem::path(modulePath.data()).parent_path() /
			L"Data" / L"SKSE" / L"Plugins" / L"DroneSwarmSkyrim.ini").wstring();
	}

	std::wstring ReadString(const std::wstring& path, const wchar_t* section, const wchar_t* key,
		const wchar_t* fallback)
	{
		std::array<wchar_t, 2048> value{};
		GetPrivateProfileStringW(section, key, fallback, value.data(),
			static_cast<DWORD>(value.size()), path.c_str());
		return value.data();
	}

	int ReadInt(const std::wstring& path, const wchar_t* key, int fallback)
	{
		return static_cast<int>(GetPrivateProfileIntW(L"Mission", key, fallback, path.c_str()));
	}

	std::string ToUtf8(const std::wstring& value)
	{
		if (value.empty()) {
			return {};
		}
		const auto size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
			nullptr, 0, nullptr, nullptr);
		if (size <= 0) {
			throw std::runtime_error("Unable to encode the configured server URL.");
		}
		std::string result(static_cast<std::size_t>(size), '\0');
		if (WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
				result.data(), size, nullptr, nullptr) != size) {
			throw std::runtime_error("Unable to encode the configured server URL.");
		}
		return result;
	}

	std::wstring FromUtf8(const std::string& value)
	{
		if (value.empty()) {
			return {};
		}
		const auto size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
			static_cast<int>(value.size()), nullptr, 0);
		if (size <= 0) {
			throw std::runtime_error("Unable to decode the configured plugin key.");
		}
		std::wstring result(static_cast<std::size_t>(size), L'\0');
		if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
				static_cast<int>(value.size()), result.data(), size) != size) {
			throw std::runtime_error("Unable to decode the configured plugin key.");
		}
		return result;
	}

	std::wstring ReadPluginKey(const std::wstring& path)
	{
		return ReadString(path, L"Server", L"PluginKey", L"");
	}

	void LoadConfiguration()
	{
		const auto path = GetIniPath();
		g_config.baseUrl = ReadString(path, L"Server", L"BaseUrl", L"http://127.0.0.1:5000");
		g_config.pluginKey = ToUtf8(ReadPluginKey(path));
		g_config.target = {
			ReadInt(path, L"TargetX", 40),
			ReadInt(path, L"TargetY", 0),
			ReadInt(path, L"TargetZ", 40)
		};
		if (g_config.pluginKey.empty()) {
			throw std::runtime_error("PluginKey is empty in Data\\SKSE\\Plugins\\DroneSwarmSkyrim.ini.");
		}
		if (g_config.baseUrl.empty()) {
			throw std::runtime_error("BaseUrl is empty in Data\\SKSE\\Plugins\\DroneSwarmSkyrim.ini.");
		}
		if (g_config.target.x < 0 || g_config.target.x >= kHorizontalCells ||
			g_config.target.y < 0 || g_config.target.y >= kVerticalCells / 2 ||
			g_config.target.z < 0 || g_config.target.z >= kHorizontalCells) {
			throw std::runtime_error("Configured target is outside the supported Skyrim mission bounds.");
		}
	}

	std::string NewGuid()
	{
		GUID guid{};
		if (FAILED(CoCreateGuid(&guid))) {
			throw std::runtime_error("Unable to create a mission ID.");
		}
		std::array<wchar_t, 40> buffer{};
		if (StringFromGUID2(guid, buffer.data(), static_cast<int>(buffer.size())) == 0) {
			throw std::runtime_error("Unable to format the mission ID.");
		}
		std::string result;
		for (const auto character : std::wstring_view(buffer.data())) {
			if (character == L'{' || character == L'}') {
				continue;
			}
			result.push_back(static_cast<char>(character));
		}
		return result;
	}

	CellPoint ToCell(const WorldPoint& position)
	{
		return {
			static_cast<int>(std::floor(position.x / kCellSize)),
			static_cast<int>(std::floor(position.z / kCellSize)),
			static_cast<int>(std::floor(position.y / kCellSize))
		};
	}

	WorldPoint ToWorld(const CellPoint& cell)
	{
		return {
			(static_cast<float>(cell.x) + 0.5F) * kCellSize,
			(static_cast<float>(cell.z) + 0.5F) * kCellSize,
			(static_cast<float>(cell.y) + 0.5F) * kCellSize
		};
	}

	std::uint64_t GameTick()
	{
		const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
			Clock::now() - g_mission.startedAt).count();
		return static_cast<std::uint64_t>(std::max<std::int64_t>(0, elapsed) * 20 / 1000);
	}

	json CellObservations(const RE::NiPoint3& position)
	{
		const auto center = ToCell({ position.x, position.y, position.z });
		json cells = json::array();
		auto* terrain = RE::TES::GetSingleton();
		if (!terrain) {
			throw std::runtime_error("Skyrim terrain service is unavailable.");
		}
		const auto maxX = g_mission.boundsMin.x + kHorizontalCells - 1;
		const auto maxY = g_mission.boundsMin.y + kVerticalCells - 1;
		const auto maxZ = g_mission.boundsMin.z + kHorizontalCells - 1;
		for (int x = std::max(g_mission.boundsMin.x, center.x - 3); x <= std::min(maxX, center.x + 3); ++x) {
			for (int z = std::max(g_mission.boundsMin.z, center.z - 3); z <= std::min(maxZ, center.z + 3); ++z) {
				const auto worldX = (static_cast<float>(x) + 0.5F) * kCellSize;
				const auto worldY = (static_cast<float>(z) + 0.5F) * kCellSize;
				float groundHeight{};
				if (!terrain->GetLandHeight(RE::NiPoint3{ worldX, worldY, 0.0F }, groundHeight)) {
					throw std::runtime_error("Landscape height is unavailable for an observed exterior cell.");
				}
				for (int y = std::max(g_mission.boundsMin.y, center.y - 3);
					y <= std::min(maxY, center.y + 3); ++y) {
					const auto cellWorldZ = (static_cast<float>(y) + 0.5F) * kCellSize;
					cells.push_back({
						{ "x", x },
						{ "y", y },
						{ "z", z },
						{ "solid", cellWorldZ <= groundHeight }
					});
				}
			}
		}
		return cells;
	}

	json MakeFrame(bool fullObservation)
	{
		json drones = json::array();
		for (const auto& drone : g_mission.drones) {
			if (!drone.reference) {
				throw std::runtime_error("A spawned Skyrim drone reference is no longer available.");
			}
			const auto position = drone.reference->GetPosition();
			const WorldPoint worldPosition{ position.x, position.y, position.z };
			json cells = json::array();
			if (fullObservation) {
				cells = CellObservations(position);
			}
			json commandResult = nullptr;
			if (!drone.terminalResults.empty()) {
				const auto& result = drone.terminalResults.front();
				commandResult = {
					{ "commandId", result.commandId },
					{ "status", result.status },
					{ "reason", result.reason.empty() ? json(nullptr) : json(result.reason) }
				};
			}
			drones.push_back({
				{ "droneId", drone.id },
				{ "availability", "available" },
				{ "position", {
					{ "x", worldPosition.x / kCellSize },
					{ "y", worldPosition.z / kCellSize },
					{ "z", worldPosition.y / kCellSize }
				} },
				{ "cells", std::move(cells) },
				{ "commandResult", std::move(commandResult) }
			});
		}
		return {
			{ "sequence", g_mission.sequence },
			{ "gameTick", GameTick() },
			{ "drones", std::move(drones) }
		};
	}

	std::string CellId(RE::FormID formId)
	{
		std::ostringstream value;
		value << std::hex << std::uppercase << std::setw(8) << std::setfill('0') << formId;
		return value.str();
	}

	void PrepareWorld()
	{
		auto* player = RE::PlayerCharacter::GetSingleton();
		if (!player || !player->GetParentCell()) {
			throw std::runtime_error("The player is not in a loaded Skyrim cell.");
		}
		auto* cell = player->GetParentCell();
		if (!cell->IsExteriorCell()) {
			throw std::runtime_error("The Skyrim adapter currently supports exterior landscape cells only.");
		}
		auto* worldSpace = cell->GetRuntimeData().worldSpace;
		if (!worldSpace) {
			throw std::runtime_error("The current exterior cell has no worldspace.");
		}
		const auto playerPosition = player->GetPosition();
		g_mission.startPosition = { playerPosition.x, playerPosition.y, playerPosition.z };
		g_mission.tower = ToCell({ playerPosition.x, playerPosition.y, playerPosition.z });
		auto* coordinates = cell->GetCoordinates();
		if (!coordinates) {
			throw std::runtime_error("The current exterior cell has no world coordinates.");
		}
		g_mission.boundsMin = {
			coordinates->cellX * kHorizontalCells,
			g_mission.tower.y - kVerticalCells / 2,
			coordinates->cellY * kHorizontalCells
		};
		const auto targetWorldX = (static_cast<float>(g_mission.boundsMin.x + g_config.target.x) + 0.5F) * kCellSize;
		const auto targetWorldY = (static_cast<float>(g_mission.boundsMin.z + g_config.target.z) + 0.5F) * kCellSize;
		auto* terrain = RE::TES::GetSingleton();
		if (!terrain) {
			throw std::runtime_error("Skyrim terrain service is unavailable.");
		}
		float targetGroundHeight{};
		if (!terrain->GetLandHeight(RE::NiPoint3{ targetWorldX, targetWorldY, 0.0F }, targetGroundHeight)) {
			throw std::runtime_error("Landscape height is unavailable at the configured target.");
		}
		const auto firstAirCellY = static_cast<int>(std::floor(targetGroundHeight / kCellSize - 0.5F)) + 1;
		g_mission.target = {
			g_mission.boundsMin.x + g_config.target.x,
			firstAirCellY + g_config.target.y,
			g_mission.boundsMin.z + g_config.target.z
		};
		if (g_mission.target.y < g_mission.boundsMin.y ||
			g_mission.target.y >= g_mission.boundsMin.y + kVerticalCells) {
			throw std::runtime_error("The configured target terrain is outside the vertical mission bounds.");
		}
		g_mission.worldId = "skyrim:worldspace:" + CellId(worldSpace->GetFormID());
		g_mission.missionId = NewGuid();
		g_mission.startedAt = Clock::now();

		g_mission.worldPrepared = true;
	}

	void SpawnDrones()
	{
		auto* player = RE::PlayerCharacter::GetSingleton();
		auto* dataHandler = RE::TESDataHandler::GetSingleton();
		auto* beeBase = dataHandler ? dataHandler->LookupForm<RE::TESBoundObject>(0xA9195, "Skyrim.esm") : nullptr;
		if (!player || !beeBase) {
			throw std::runtime_error("Skyrim's Bee base object 000A9195 was not found.");
		}

		g_mission.drones.clear();
		g_mission.drones.reserve(kDroneCount);
		std::vector<CellPoint> spawnCells;
		auto* terrain = RE::TES::GetSingleton();
		if (!terrain) {
			throw std::runtime_error("Skyrim terrain service is unavailable.");
		}
		for (int radius = 0; radius <= 6 && spawnCells.size() < kDroneCount; ++radius) {
			for (int dx = -radius; dx <= radius && spawnCells.size() < kDroneCount; ++dx) {
				for (int dz = -radius; dz <= radius && spawnCells.size() < kDroneCount; ++dz) {
					if (std::abs(dx) + std::abs(dz) != radius) {
						continue;
					}
					CellPoint candidate{ g_mission.tower.x + dx, g_mission.tower.y, g_mission.tower.z + dz };
					if (candidate.x < g_mission.boundsMin.x ||
						candidate.x >= g_mission.boundsMin.x + kHorizontalCells ||
						candidate.z < g_mission.boundsMin.z ||
						candidate.z >= g_mission.boundsMin.z + kHorizontalCells) {
						continue;
					}
					const auto world = ToWorld(candidate);
					float groundHeight{};
					if (!terrain->GetLandHeight(RE::NiPoint3{ world.x, world.y, 0.0F }, groundHeight)) {
						continue;
					}
					const auto altitude = std::max(g_mission.startPosition.z + 16.0F, groundHeight + 16.0F);
					candidate.y = static_cast<int>(std::floor(altitude / kCellSize));
					if (candidate.y < g_mission.boundsMin.y ||
						candidate.y >= g_mission.boundsMin.y + kVerticalCells) {
						continue;
					}
					spawnCells.push_back(candidate);
				}
			}
		}
		if (spawnCells.size() != kDroneCount) {
			throw std::runtime_error("Could not find five drone start cells inside the current exterior cell.");
		}
		for (std::size_t index = 0; index < kDroneCount; ++index) {
			const auto position = ToWorld(spawnCells[index]);
			const auto worldPosition = WorldPoint{ position.x, position.y, position.z };
			auto reference = player->PlaceObjectAtMe(beeBase, false);
			if (!reference) {
				throw std::runtime_error("Skyrim failed to spawn a drone reference.");
			}
			reference->SetCollision(false);
			reference->SetScale(0.7F);
			reference->SetPosition(RE::NiPoint3{ worldPosition.x, worldPosition.y, worldPosition.z });
			g_mission.drones.push_back({
				"SKYRIM-" + (index < 9 ? std::string("0") : std::string()) + std::to_string(index + 1),
				std::move(reference)
			});
		}
	}

	std::string InitializeBody()
	{
		return json{
			{ "protocolVersion", 1 },
			{ "continuousFlight", true },
			{ "missionId", g_mission.missionId },
			{ "worldId", g_mission.worldId },
			{ "boundsMin", {
				{ "x", g_mission.boundsMin.x },
				{ "y", g_mission.boundsMin.y },
				{ "z", g_mission.boundsMin.z }
			} },
			{ "tower", {
				{ "x", g_mission.tower.x },
				{ "y", g_mission.tower.y },
				{ "z", g_mission.tower.z }
			} },
			{ "target", {
				{ "x", g_mission.target.x },
				{ "y", g_mission.target.y },
				{ "z", g_mission.target.z }
			} },
			{ "initialFrame", MakeFrame(true) }
		}.dump();
	}

	bool HttpRequest(const Request& request, Response& response)
	{
		URL_COMPONENTS components{};
		components.dwStructSize = sizeof(components);
		components.dwSchemeLength = static_cast<DWORD>(-1);
		components.dwHostNameLength = static_cast<DWORD>(-1);
		components.dwUrlPathLength = static_cast<DWORD>(-1);
		auto baseUrl = g_config.baseUrl;
		if (!WinHttpCrackUrl(baseUrl.c_str(), static_cast<DWORD>(baseUrl.size()), 0, &components)) {
			response.error = "BaseUrl is not a valid HTTP(S) URL.";
			return false;
		}
		if (components.nScheme != INTERNET_SCHEME_HTTP && components.nScheme != INTERNET_SCHEME_HTTPS) {
			response.error = "BaseUrl must use HTTP or HTTPS.";
			return false;
		}
		const std::wstring host(components.lpszHostName, components.dwHostNameLength);

		const auto session = WinHttpOpen(L"DroneSwarmSkyrim/0.1",
			WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
		if (!session) {
			response.error = "WinHTTP could not open a session.";
			return false;
		}
		WinHttpSetTimeouts(session, 3000, 3000, 3000, 3000);
		const auto connection = WinHttpConnect(session, host.c_str(), components.nPort, 0);
		if (!connection) {
			response.error = "WinHTTP could not connect to the configured backend.";
			WinHttpCloseHandle(session);
			return false;
		}
		const auto requestHandle = WinHttpOpenRequest(connection,
			request.kind == RequestKind::Status ? L"GET" :
				(request.kind == RequestKind::Observation || request.kind == RequestKind::Telemetry ||
					request.kind == RequestKind::Initialize ? L"POST" : L"GET"),
			request.path.c_str(), nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
			components.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0);
		if (!requestHandle) {
			response.error = "WinHTTP could not create the backend request.";
			WinHttpCloseHandle(connection);
			WinHttpCloseHandle(session);
			return false;
		}

		const auto wideKey = FromUtf8(g_config.pluginKey);
		const auto headers = std::wstring(L"Content-Type: application/json\r\nX-Plugin-Key: ") +
			wideKey + L"\r\n";
		auto body = request.body;
		const auto bodySize = static_cast<DWORD>(body.size());
		const auto sent = WinHttpSendRequest(requestHandle, headers.c_str(),
			static_cast<DWORD>(headers.size()), body.empty() ? WINHTTP_NO_REQUEST_DATA : body.data(),
			bodySize, bodySize, 0);
		if (!sent || !WinHttpReceiveResponse(requestHandle, nullptr)) {
			response.error = "WinHTTP failed while sending or receiving the backend request.";
			WinHttpCloseHandle(requestHandle);
			WinHttpCloseHandle(connection);
			WinHttpCloseHandle(session);
			return false;
		}

		DWORD statusCode{};
		DWORD statusSize = sizeof(statusCode);
		if (!WinHttpQueryHeaders(requestHandle, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
				WINHTTP_HEADER_NAME_BY_INDEX, &statusCode, &statusSize, WINHTTP_NO_HEADER_INDEX)) {
			response.error = "WinHTTP could not read the backend response status.";
			WinHttpCloseHandle(requestHandle);
			WinHttpCloseHandle(connection);
			WinHttpCloseHandle(session);
			return false;
		}
		response.statusCode = statusCode;
		for (;;) {
			DWORD available{};
			if (!WinHttpQueryDataAvailable(requestHandle, &available)) {
				response.error = "WinHTTP could not read the backend response body.";
				WinHttpCloseHandle(requestHandle);
				WinHttpCloseHandle(connection);
				WinHttpCloseHandle(session);
				return false;
			}
			if (available == 0) {
				break;
			}
			std::string buffer(available, '\0');
			DWORD read{};
			if (!WinHttpReadData(requestHandle, buffer.data(), available, &read)) {
				response.error = "WinHTTP failed while reading the backend response body.";
				WinHttpCloseHandle(requestHandle);
				WinHttpCloseHandle(connection);
				WinHttpCloseHandle(session);
				return false;
			}
			buffer.resize(read);
			response.body += buffer;
		}
		WinHttpCloseHandle(requestHandle);
		WinHttpCloseHandle(connection);
		WinHttpCloseHandle(session);
		if (statusCode < 200 || statusCode >= 300) {
			response.error = "Backend returned HTTP " + std::to_string(statusCode) + ": " + response.body;
			return false;
		}
		response.success = true;
		return true;
	}

	void NetworkWorker(std::stop_token stopToken)
	{
		while (!stopToken.stop_requested() && !g_networkStopping.load()) {
			Request request;
			{
				std::unique_lock lock(g_queueMutex);
				g_queueChanged.wait(lock, stopToken, [&] {
					return stopToken.stop_requested() || g_networkStopping.load() || !g_requests.empty();
				});
				if (stopToken.stop_requested() || g_networkStopping.load()) {
					return;
				}
				request = std::move(g_requests.front());
				g_requests.pop_front();
			}

			Response response;
			response.kind = request.kind;
			for (int attempt = 1; attempt <= 3; ++attempt) {
				response = {};
				response.kind = request.kind;
				if (HttpRequest(request, response)) {
					break;
				}
				if (response.statusCode != 0 && response.statusCode < 500) {
					break;
				}
				if (attempt < 3) {
					std::this_thread::sleep_for(std::chrono::milliseconds(250 * attempt));
				}
			}
			{
				std::scoped_lock lock(g_queueMutex);
				g_responses.push_back(std::move(response));
			}
			g_requestInFlight.store(false);
		}
	}

	bool Submit(Request request)
	{
		if (g_requestInFlight.exchange(true)) {
			return false;
		}
		{
			std::scoped_lock lock(g_queueMutex);
			g_requests.push_back(std::move(request));
		}
		g_queueChanged.notify_one();
		return true;
	}

	std::deque<Response> DrainResponses()
	{
		std::scoped_lock lock(g_queueMutex);
		std::deque<Response> responses;
		responses.swap(g_responses);
		return responses;
	}

	Drone* FindDrone(const std::string& id)
	{
		const auto found = std::find_if(g_mission.drones.begin(), g_mission.drones.end(),
			[&](const Drone& drone) { return drone.id == id; });
		return found == g_mission.drones.end() ? nullptr : &*found;
	}

	void AcceptCommands(const json& response)
	{
		for (const auto& acknowledgement : response.at("acknowledgedResults")) {
			const auto id = acknowledgement.at("droneId").get<std::string>();
			const auto commandId = acknowledgement.at("commandId").get<std::string>();
			auto* drone = FindDrone(id);
			if (!drone || drone->terminalResults.empty() ||
				drone->terminalResults.front().commandId != commandId) {
				throw std::runtime_error("The backend acknowledged an unknown or out-of-order command result.");
			}
			drone->terminalResults.pop_front();
			drone->completedCommandIds.erase(commandId);
		}

		for (const auto& item : response.at("commands")) {
			auto* drone = FindDrone(item.at("droneId").get<std::string>());
			if (!drone) {
				throw std::runtime_error("The backend returned a command for an unknown Skyrim drone.");
			}
			if (item.at("kind").get<std::string>() != "move") {
				throw std::runtime_error("The backend returned an unsupported Skyrim command kind.");
			}
			FlightCommand command{
				item.at("commandId").get<std::string>(),
				{
					item.at("destination").at("x").get<int>(),
					item.at("destination").at("y").get<int>(),
					item.at("destination").at("z").get<int>()
				},
				std::nullopt
			};
			if (item.contains("afterCommandId") && !item.at("afterCommandId").is_null()) {
				command.afterCommandId = item.at("afterCommandId").get<std::string>();
			}
			if (drone->completedCommandIds.contains(command.commandId)) {
				continue;
			}
			if (command.afterCommandId) {
				if (drone->successor && drone->successor->commandId == command.commandId) {
					continue;
				}
				if (drone->currentCommand && drone->currentCommand->commandId == *command.afterCommandId) {
					drone->successor = std::move(command);
				}
				continue;
			}
			if (drone->currentCommand && drone->currentCommand->commandId == command.commandId) {
				continue;
			}
			if (!drone->currentCommand) {
				drone->currentCommand = std::move(command);
			}
		}
	}

	void HandleResponse(Response& response)
	{
		if (!response.success) {
			SKSE::log::error("Skyrim backend request failed: {}", response.error);
			g_mission.stopped = true;
			return;
		}
		try {
			const auto body = response.body.empty() ? json::object() : json::parse(response.body);
			if (response.kind == RequestKind::Status) {
				if (!body.at("activeMissionId").is_null()) {
					throw std::runtime_error("A Skyrim mission is already active. End or reset it before starting this adapter.");
				}
				SpawnDrones();
				if (!Submit({ RequestKind::Initialize, L"/api/plugin/skyrim/missions", InitializeBody() })) {
					throw std::runtime_error("Could not enqueue Skyrim mission initialization.");
				}
				return;
			}
			if (response.kind == RequestKind::Initialize) {
				if (body.at("missionId").get<std::string>() != g_mission.missionId) {
					throw std::runtime_error("The backend initialized a different Skyrim mission ID.");
				}
				g_mission.initialized = true;
				g_mission.sequence = 1;
				g_mission.nextFrameAt = Clock::now() + std::chrono::milliseconds(100);
				g_mission.nextObservationAt = Clock::now() + std::chrono::milliseconds(500);
				SKSE::log::info("Skyrim mission {} initialized.", g_mission.missionId);
				return;
			}
			if (body.at("missionId").get<std::string>() != g_mission.missionId ||
				body.at("sequence").get<std::uint64_t>() != g_mission.sequence) {
				throw std::runtime_error("The backend response did not match the outstanding Skyrim frame.");
			}
			AcceptCommands(body);
			++g_mission.sequence;
		} catch (const std::exception& error) {
			SKSE::log::error("Invalid Skyrim backend response: {}", error.what());
			g_mission.stopped = true;
		}
	}

	void FinishCurrentCommand(Drone& drone, std::string status, std::string reason = {})
	{
		if (!drone.currentCommand) {
			return;
		}
		drone.completedCommandIds.insert(drone.currentCommand->commandId);
		drone.terminalResults.push_back({
			drone.currentCommand->commandId,
			std::move(status),
			std::move(reason)
		});
		const auto completed = drone.terminalResults.back().status == "completed";
		drone.currentCommand.reset();
		if (completed && drone.successor) {
			drone.currentCommand = std::move(drone.successor);
			drone.successor.reset();
		} else {
			drone.successor.reset();
		}
	}

	void AdvanceFlights()
	{
		const auto now = Clock::now();
		const auto elapsed = g_lastMovementUpdate.time_since_epoch().count() == 0
			? 0.0F
			: std::chrono::duration<float>(now - g_lastMovementUpdate).count();
		g_lastMovementUpdate = now;
		auto* terrain = RE::TES::GetSingleton();
		for (auto& drone : g_mission.drones) {
			if (!drone.reference || !drone.currentCommand) {
				continue;
			}
			const auto current = drone.reference->GetPosition();
			const auto currentCell = ToCell({ current.x, current.y, current.z });
			const auto& destination = drone.currentCommand->destination;
			const auto distanceInCells = std::abs(destination.x - currentCell.x) +
				std::abs(destination.y - currentCell.y) + std::abs(destination.z - currentCell.z);
			if (distanceInCells != 1) {
				FinishCurrentCommand(drone, "blocked", "Planner destination is not face-adjacent.");
				continue;
			}
			const auto target = ToWorld(destination);
			const auto dx = target.x - current.x;
			const auto dy = target.y - current.y;
			const auto dz = target.z - current.z;
			const auto distance = std::sqrt(dx * dx + dy * dy + dz * dz);
			const auto step = 72.0F * elapsed;
			const auto fraction = distance <= step || distance < 0.01F ? 1.0F : step / distance;
			const WorldPoint next{
				current.x + dx * fraction,
				current.y + dy * fraction,
				current.z + dz * fraction
			};
			float groundHeight{};
			if (!terrain || !terrain->GetLandHeight(RE::NiPoint3{ next.x, next.y, 0.0F }, groundHeight)) {
				FinishCurrentCommand(drone, "blocked", "Landscape height is unavailable at the next flight position.");
				continue;
			}
			if (next.z <= groundHeight + 8.0F) {
				FinishCurrentCommand(drone, "blocked", "Destination intersects Skyrim landscape.");
				continue;
			}
			drone.reference->SetPosition(RE::NiPoint3{ next.x, next.y, next.z });
			if (fraction >= 1.0F) {
				FinishCurrentCommand(drone, "completed");
			}
		}
	}

	void StartMission()
	{
		PrepareWorld();
		if (g_mission.target.x == g_mission.tower.x &&
			g_mission.target.y == g_mission.tower.y &&
			g_mission.target.z == g_mission.tower.z) {
			throw std::runtime_error("The configured target cannot overlap the tower.");
		}
		if (!Submit({ RequestKind::Status, L"/api/plugin/skyrim/status", {} })) {
			throw std::runtime_error("Could not enqueue the Skyrim backend status request.");
		}
		SKSE::log::info("Prepared mission bounds for exterior worldspace {}.", g_mission.worldId);
	}

	void Pump()
	{
		if (!g_gameReady || g_mission.stopped) {
			return;
		}
		if (!g_mission.worldPrepared) {
			StartMission();
		}
		for (auto& response : DrainResponses()) {
			HandleResponse(response);
			if (g_mission.stopped) {
				return;
			}
		}
		if (!g_mission.initialized) {
			return;
		}
		AdvanceFlights();
		if (g_requestInFlight.load() || Clock::now() < g_mission.nextFrameAt) {
			return;
		}
		const auto now = Clock::now();
		const auto fullObservation = now >= g_mission.nextObservationAt;
		try {
			const auto kind = fullObservation ? RequestKind::Observation : RequestKind::Telemetry;
			const auto path = fullObservation
				? L"/api/plugin/skyrim/missions/" + std::wstring(g_mission.missionId.begin(), g_mission.missionId.end()) + L"/observations"
				: L"/api/plugin/skyrim/missions/" + std::wstring(g_mission.missionId.begin(), g_mission.missionId.end()) + L"/telemetry";
			const auto frame = MakeFrame(fullObservation);
			if (Submit({ kind, path, frame.dump() })) {
				g_mission.nextFrameAt = now + std::chrono::milliseconds(100);
				if (fullObservation) {
					g_mission.nextObservationAt = now + std::chrono::milliseconds(500);
				}
			}
		} catch (const std::exception& error) {
			SKSE::log::error("Could not build a valid Skyrim observation frame: {}", error.what());
			g_mission.stopped = true;
		}
	}

	void SchedulePump();

	void RunPump()
	{
		try {
			Pump();
		} catch (const std::exception& error) {
			SKSE::log::error("Skyrim adapter stopped: {}", error.what());
			g_mission.stopped = true;
		}
		SchedulePump();
	}

	void SchedulePump()
	{
		if (g_pumpScheduled || !g_gameReady || g_mission.stopped) {
			return;
		}
		const auto* taskInterface = SKSE::GetTaskInterface();
		if (!taskInterface) {
			SKSE::log::error("SKSE task interface is unavailable.");
			g_mission.stopped = true;
			return;
		}
		g_pumpScheduled = true;
		taskInterface->AddTask([] {
			g_pumpScheduled = false;
			RunPump();
		});
	}

	void OnMessage(SKSE::MessagingInterface::Message* message)
	{
		if (message->type == SKSE::MessagingInterface::kPostLoadGame) {
			g_gameReady = true;
			SchedulePump();
		}
	}
}

SKSEPluginLoad(const SKSE::LoadInterface* skse)
{
	auto logsFolder = SKSE::log::log_directory();
	if (!logsFolder) {
		return false;
	}
	auto sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(
		(*logsFolder / "DroneSwarmSkyrim.log").string(), true);
	auto logger = std::make_shared<spdlog::logger>("DroneSwarmSkyrim", std::move(sink));
	spdlog::set_default_logger(std::move(logger));
	spdlog::set_level(spdlog::level::info);
	spdlog::flush_on(spdlog::level::info);

	SKSE::Init(skse);
	try {
		LoadConfiguration();
	} catch (const std::exception& error) {
		SKSE::log::error("Skyrim adapter configuration error: {}", error.what());
		return false;
	}
	g_networkThread = std::jthread(NetworkWorker);
	if (!SKSE::GetMessagingInterface()->RegisterListener(OnMessage)) {
		SKSE::log::error("Could not register the Skyrim load-game listener.");
		g_networkStopping.store(true);
		g_networkThread.request_stop();
		g_queueChanged.notify_all();
		return false;
	}
	SKSE::log::info("Drone Swarm Skyrim adapter loaded.");
	return true;
}
