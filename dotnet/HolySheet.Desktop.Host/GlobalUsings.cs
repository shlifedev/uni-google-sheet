// The engine has its own small JSON types (HolySheet.Core.Config.JsonObject, JsonValue) for holysheet.json.
// The host speaks System.Text.Json; these aliases decide every ambiguity in its favour.
global using JsonArray = System.Text.Json.Nodes.JsonArray;
global using JsonNode = System.Text.Json.Nodes.JsonNode;
global using JsonObject = System.Text.Json.Nodes.JsonObject;
global using JsonValue = System.Text.Json.Nodes.JsonValue;
