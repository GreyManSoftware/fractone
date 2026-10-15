// Pi extension for FracTone: starts the fractone MCP server and asks the user before any call that changes the FM9.
// Pi has no permission prompts of its own, so this is the gate; the server's own safeguards (settings file) still apply.
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

const SERVER = "fractone";
const PREFIX = `mcp__${SERVER}__`;
// The tools the server marks destructive: they change what the FM9 holds or plays.
// reamp plays through the speakers, so it is confirmed too.
const CONFIRM = new Set(["store_preset", "store_edit_buffer", "audition", "select_preset", "reamp"]);

export default function (pi: ExtensionAPI) {
	// A same-named server in the user's mcp.json takes precedence (e.g. one pointing at a local build).
	pi.registerMcpServer(SERVER, {
		command: "dnx",
		args: ["FracTone@0.16.1"],
		exposure: "direct",
		timeout: 600,
		description: "Fractal Audio FM9: presets, favourites, search, tone design and tone matching",
	});

	pi.on("tool_call", async (event, ctx) => {
		if (!event.toolName.startsWith(PREFIX)) return undefined;
		const tool = event.toolName.slice(PREFIX.length);
		if (!CONFIRM.has(tool)) return undefined;
		if (!ctx.hasUI) return { block: true, reason: `${tool} changes the FM9 and needs the user's confirmation, which is not possible without a UI.` };
		const ok = await ctx.ui.confirm(`Allow ${tool.replace("fm9_", "FM9 ").replaceAll("_", " ")}?`, JSON.stringify(event.input, null, 2));
		return ok ? undefined : { block: true, reason: `The user declined ${tool}.` };
	});
}
