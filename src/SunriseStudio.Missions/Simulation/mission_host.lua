-- Solforge mission host: checks one mission script and walks it through a fake game host.
-- Built from tools/mission-sim/walk2.lua (the Red War walker); MissionHost.cs runs it in a bundled
-- Lua 5.4 (KeraLua).
-- Inputs, set as globals: SCRIPTS_ROOT (the folder require starts from), SDK_ROOT (the game's
-- sdk/lua folder), MODULE (the script's require name), SOURCE_PATH (the script file) and MODE:
-- "check", or a simulation mode: play, skip, cutscene_never_loads or rejoin.
-- Returns one string of records, one per line: kind TAB line TAB text (line 0 = no line).
-- Kinds: syntax, key, hash, check, error, stall, problem, mock, step, trace, module, info, summary.

local records = {}
local function rec(kind, line, text)
    text = tostring(text):gsub("[\r\n\t]+", " ")
    records[#records + 1] = kind .. "\t" .. tostring(line or 0) .. "\t" .. text
end
local function finish()
    debug.sethook()
    return table.concat(records, "\n")
end

-- The script under test only: other files (lib, SDK) never name a line.
local function norm(path) return (path:gsub("\\", "/"):lower()) end
local source_norm = norm(SOURCE_PATH)
local source_lines = {}
do
    local file = io.open(SOURCE_PATH, "rb")
    if file == nil then rec("error", 0, "cannot read " .. SOURCE_PATH); return finish() end
    local text = file:read("a")
    file:close()
    for line in (text:gsub("\r", "") .. "\n"):gmatch("([^\n]*)\n") do source_lines[#source_lines + 1] = line end
end
local function is_script(info)
    return info ~= nil and info.source:sub(1, 1) == "@" and norm(info.source:sub(2)) == source_norm
end
-- The innermost script line on the stack from this level up; 0 when the script is not on it.
local function script_line_from(level)
    for at = level + 1, level + 60 do
        local info = debug.getinfo(at, "Sl")
        if info == nil then break end
        if is_script(info) then return info.currentline end
    end
    return 0
end
-- First line whose code holds the quoted text; 0 when none.
local function line_of(id)
    if id == nil then return 0 end
    local quoted = '"' .. tostring(id) .. '"'
    for number, line in ipairs(source_lines) do
        if line:find(quoted, 1, true) then return number end
    end
    return 0
end
-- A message names a step, leg or encounter id; its line beats the stack's.
local function best_line(text, fallback)
    for word in text:gmatch("[%w_%.%-]+") do
        if #word >= 3 then
            local number = line_of(word)
            if number > 0 then return number end
        end
    end
    return fallback
end
-- Splits "path/file.lua:12: text" into text and "file.lua:12".
local function clean(message)
    local file, number, rest = message:match("([^/\\%s]+%.lua):(%d+): (.*)$")
    if rest ~= nil then return rest, file .. ":" .. number end
    return message, nil
end
local function handler(message)
    return {message = tostring(message), line = script_line_from(1)}
end

-- No processes, no native modules; a script that loops forever stops.
os.execute, io.popen = nil, nil
package.cpath = ""
package.path = SCRIPTS_ROOT .. "/?.lua;" .. SCRIPTS_ROOT .. "/?/init.lua;" .. SDK_ROOT .. "/?.lua;" .. SDK_ROOT .. "/?/init.lua"
debug.sethook(function() error("instruction limit reached; the script may loop forever", 2) end, "", 200000000)

-- Syntax with the real Lua 5.4 reader.
do
    local _, problem = loadfile(SOURCE_PATH)
    if problem ~= nil then
        rec("syntax", tonumber(problem:match(":(%d+): [^:]*$") or problem:match(":(%d+):")) or 0, (clean(problem)))
        return finish()
    end
end

-- Generated mission modules answer every key the script names; a missing one is recorded with its
-- line. Every number and id string they hold is game data the script's literals must match.
local values, modules, reported = {}, {}, {}
local function collect(t, depth, seen)
    if seen[t] or depth > 8 then return end
    seen[t] = true
    for _, value in next, t do
        local kind = type(value)
        if kind == "number" or kind == "string" then values[value] = true
        elseif kind == "table" then collect(value, depth + 1, seen) end
    end
end
local function strict(t, path, module, depth, seen)
    if seen[t] or depth > 3 or getmetatable(t) ~= nil then return end
    seen[t] = true
    for key, value in next, t do
        if type(key) == "string" and type(value) == "table" and key:match("^%u") then
            strict(value, path .. "." .. key, module, depth + 1, seen)
        end
    end
    setmetatable(t, {__index = function(_, key)
        local info = debug.getinfo(2, "Sl")
        if is_script(info) and type(key) == "string" then
            local name = path .. "." .. key
            local once = name .. "@" .. info.currentline
            if not reported[once] then
                reported[once] = true
                rec("key", info.currentline, name .. " does not exist in the game data (" .. module .. ")")
            end
        end
        return nil
    end})
end
local real_require = require
function require(name)
    local loaded = real_require(name)
    if type(loaded) == "table" and type(name) == "string" and modules[name] == nil
        and (name == "missions" or name == "activities" or name:match("^missions%.") or name:match("^activities%.")) then
        modules[name] = loaded
        collect(loaded, 0, {})
        strict(loaded, name:match("%.") and "mission" or name, name, 0, {})
        rec("module", 0, type(loaded.tag) == "number" and string.format("%s 0x%08X", name, loaded.tag) or name)
    end
    return loaded
end

-- Campaign scripts: keep the content table campaign.new is given (its check() runs inside new).
local captured
do
    local ok, campaign = pcall(require, "lib.campaign")
    if ok and type(campaign) == "table" and type(campaign.new) == "function" then
        local real_new = campaign.new
        campaign.new = function(content) captured = content; return real_new(content) end
    end
end

local ok, prog = xpcall(require, handler, MODULE)
if not ok then
    local text, origin = clean(prog.message)
    -- An assert in the script library (lib/campaign.lua check(), lib.one) is a content check.
    local file = origin and origin:match("^(.-):%d+$")
    local in_lib = file ~= nil and io.open(SCRIPTS_ROOT .. "/lib/" .. file, "rb")
    if in_lib then in_lib:close() end
    local kind = in_lib and "check" or "error"
    rec(kind, best_line(text, prog.line), text .. (origin and (" (" .. origin .. ")") or ""))
end

-- Hash and id literals the script writes by hand must be in the mission's game data.
if next(modules) ~= nil then
    for number, line in ipairs(source_lines) do
        local code = line:gsub("%-%-.*$", "")
        for hex in code:gmatch("0[xX](%x+)") do
            if #hex >= 6 and not values[tonumber(hex, 16)] then
                rec("hash", number, "0x" .. hex:upper() .. " is not a hash in the game data")
            end
        end
        for id, kind in code:gmatch('"(([%l_]+)/[%x/]+)"') do
            if (kind == "slot" or kind == "squad" or kind == "state" or kind == "scene") and not values[id] then
                rec("hash", number, id .. " is not an id in the game data")
            end
        end
    end
end

local content = captured
if content ~= nil then
    rec("info", 0, string.format("campaign %s: %d legs, %d steps, %d encounters, %d intro and %d outro cutscenes",
        tostring(content.key), #(content.legs or {}), #(content.steps or {}), #(content.encounters or {}),
        #(content.intro or {}), #(content.outro or {})))
end
if MODE == "check" or not ok then return finish() end
if type(prog) ~= "table" then rec("error", 0, MODULE .. " does not return a mission table"); return finish() end

-------------------------------------------------------------------------------------------------
-- Simulation: walk2's fake host. Every step's own end is satisfied in turn; a step that never
-- ends is a stall. Anything the script asks of the host that the host does not model is a mock.
-------------------------------------------------------------------------------------------------
local mode = MODE
local mocked = {}
local function missing_mock(owner, name)
    local key = owner .. ":" .. tostring(name)
    if not mocked[key] then
        mocked[key] = true
        rec("mock", script_line_from(2), key .. " is not simulated (the fake host did nothing)")
    end
end
local function catch_all(t, owner)
    return setmetatable(t, {__index = function(_, name)
        missing_mock(owner, name)
        return function() end
    end})
end

local vars, placed, dead, done = {}, {}, {}, {}
local completes = 0
local function trace(text) rec("trace", 0, text) end
local state = catch_all({variable = function(_, k) return vars[k] end}, "state")
-- Slot calls the host models; any other method is a missing mock.
local known_slot = {set_cinematic_active = true, set_directive = true, play_dialogue_cue = true, transition = true,
    fire_trigger = true, disarm_trigger = true, assign_combat_objective = true, set_ghost_link = true,
    clear_directives = true, set_interactable_object = true, set_occupancy_condition = true, play_sequence = true}
local function slot(id)
    return setmetatable({id = id}, {__index = function(_, method)
        if not known_slot[method] then missing_mock("slot", method) end
        return function(_, args)
            args = args or {}
            if method == "play_dialogue_cue" then trace("LINE " .. id) end
            if method == "transition" then trace("MOVE " .. id) end
            if method == "set_cinematic_active" then trace("CINE " .. (args.active and "on " or "off ") .. id) end
            if method == "set_directive" then
                local d = args.directive or {}
                trace("GOAL " .. tostring(d.title) .. (d.progress and (" / " .. d.progress) or "")
                    .. (type(args.progress) == "table" and (" [" .. table.concat(args.progress, " of ") .. "]")
                        or type(args.progress) == "number" and (" [" .. args.progress .. " of " .. tostring(args.progress_target) .. ", plain]")
                        or "")
                    .. (args.timer_seconds and (" (" .. args.timer_seconds .. "s)") or ""))
                assert(type(args.progress) ~= "table" or d.counter, "list progress on a directive without a counter")
            end
        end
    end})
end
local timers = {}
local ctx = {attempt_generation = 3, sdk = {device_transitions = setmetatable({}, {__index = function(_, k) return k end})}}
function ctx:set_variable(k, v) vars[k] = v end
function ctx:clear_variable(k) vars[k] = nil end
function ctx:slot(id) return slot(id) end
function ctx:squad(sq) return catch_all({place = function() placed[sq] = true end}, "squad") end
function ctx:cohort(a)
    for _, sq in ipairs(a.squads) do if not placed[sq] or not dead[sq] then return {cleared = false} end end
    return {cleared = true}
end
function ctx:scene(id)
    assert(type(id) == "string", "scene id")
    return catch_all({
        activate = function(_, a) for k in next, a do assert(k == "spawn", "activate key " .. k) end
            trace("SCENE " .. id .. (a.spawn and " +cast" or "")) end,
        send_event = function(_, a) assert(type(a.key) == "number", "send_event key"); trace("INPUT " .. id .. string.format(" %08X", a.key)) end,
    }, "scene")
end
function ctx:select_state() end
function ctx:start_timer(name, ms) assert(type(name) == "string" and math.type(ms) == "integer", "start_timer(name, integer ms)"); timers[name] = true end
function ctx:hold_spawn() end
function ctx:activate_objects(a) assert(type(a.slots) == "table", "activate_objects slots") end
function ctx:cancel_timer(name) timers[name] = nil end
function ctx:complete_mission() trace("COMPLETE"); completes = completes + 1 end
function ctx:grant_reward() return 1 end
function ctx:log(t)
    local id = tostring(t):match("step (%S+) done")
    if id then done[id] = true end
end
catch_all(ctx, "context")

local failed
local function run(name, f, ...)
    if failed then return end
    local good, problem = xpcall(f, handler, ...)
    if not good then
        failed = true
        local text, origin = clean(problem.message)
        rec("error", problem.line, name .. ": " .. text .. (origin and (" (" .. origin .. ")") or ""))
    end
end
local function ev(f) f.attempt_generation = 3; return f end
local function call(h, f) if type(prog[h]) == "function" then run(h, prog[h], ctx, state, ev(f)) end end
local function lifecycle(h) if type(prog[h]) == "function" then run(h, prog[h], ctx, state) end end
local function list(v) if v == nil then return {} end if type(v) == "table" then return v end return {v} end
local function fire_timers()
    for name in next, timers do timers[name] = nil; call("on_event_timer_elapsed", {timer_name = name}) end
end
local function kill()
    local n = 0
    for sq in next, placed do if not dead[sq] then dead[sq] = true; n = n + 1 end end
    call("on_event_squad_state", {previous_alive_count = n, alive_count = 0})
end

lifecycle("on_start")
if content == nil then
    lifecycle("on_load")
    rec("info", 0, "No campaign.new content was captured, so there are no steps to walk; the simulator walks "
        .. "lib/campaign.lua scripts and only ran on_start and on_load here.")
    if failed then rec("stall", 0, "stopped by a script error") end
    return finish()
end

local c = content
for _, cut in ipairs(c.intro or {}) do
    call("on_event_region_changed", {region_index = cut.state.region_index})
    call("on_event_cinematic_terminated", {slot = {id = cut.cinematic}})
end
local region = c.legs[1].state.region_index
call("on_event_client_state_changed", {entered = true, held_region_index = region})
local legs = {}
for _, leg in ipairs(c.legs) do legs[leg.id] = leg end
local stuck
local rejoin_at = math.max(1, math.ceil(#c.steps / 2))
for index, step in ipairs(c.steps) do
    if failed then break end
    rec("step", line_of(step.id), step.id)
    if mode == "rejoin" and index == rejoin_at then
        trace("REJOIN: the player leaves and reattaches (on_load, client entered)")
        lifecycle("on_load")
        call("on_event_client_state_changed", {entered = true, held_region_index = region})
    end
    for _ = 1, 3 do fire_timers() end
    local e = step.ends or {}
    -- Fire encounter triggers so the fights a clear waits on are placed.
    for _, enc in ipairs(c.encounters or {}) do
        local src = enc.trigger or enc.monitor
        if src then call(enc.trigger and "on_event_player_trigger" or "on_event_trigger_entered", {slot = {id = src}}) end
    end
    if e.region then
        region = legs[e.region].state.region_index
        call("on_event_region_changed", {region_index = region})
    end
    if e.clear or e.counted then kill() end
    for _, o in ipairs(e.destroyed or {}) do call("on_event_object_state", {slot = {id = o}, present = false}) end
    if e.ghost_link then
        call("on_event_ghost_link_state", {slot = {id = e.ghost_link}, active = true, progress = 0.5})
        call("on_event_ghost_link_state", {slot = {id = e.ghost_link}, active = false, progress = 1})
    end
    if e.interact then call("on_event_object_interacted", {slot = {id = e.interact}}) end
    if e.scene then call("on_event_scene_finished", {slot = {id = e.scene}}) end
    if e.health then call("on_event_damage_state", {slot = {id = e.health.slot}, health = 0}) end
    for _, m in ipairs(list(e.monitor)) do call("on_event_trigger_entered", {slot = {id = m}}) end
    for _, t in ipairs(list(e.trigger)) do call("on_event_player_trigger", {slot = {id = t}}) end
    -- A leg's region report arms its triggers; send each leg as the step's triggers would need it.
    if not done[step.id] then
        for _, leg in ipairs(c.legs) do call("on_event_region_changed", {region_index = leg.state.region_index}) end
        for _, t in ipairs(list(e.trigger)) do call("on_event_player_trigger", {slot = {id = t}}) end
        for _, m in ipairs(list(e.monitor)) do call("on_event_trigger_entered", {slot = {id = m}}) end
        if e.clear or e.counted then kill() end
    end
    if not done[step.id] and not failed then stuck = step; break end
end

if not stuck and not failed then
    for _, cut in ipairs(c.outro or {}) do
        if cut.state ~= nil and cut.cinematic ~= nil then
            if mode == "skip" then
                call("on_event_region_changed", {region_index = cut.state.region_index})
                call("on_event_cinematic_skip_requested", {slot = {id = cut.cinematic}})
                call("on_event_cinematic_terminated", {slot = {id = cut.cinematic}})
            elseif mode ~= "cutscene_never_loads" then
                call("on_event_region_changed", {region_index = cut.state.region_index})
                call("on_event_cinematic_terminated", {slot = {id = cut.cinematic}})
            end
        else
            fire_timers()
        end
    end
    if mode == "cutscene_never_loads" then
        trace("The outro cutscene region never loads; pending timers fire")
        fire_timers()
    end
    if mode == "rejoin" then
        trace("REJOIN after the end (on_load twice)")
        lifecycle("on_load"); lifecycle("on_load")
    end
end

if failed then rec("stall", 0, "stopped by a script error (see the error above)") end
if stuck then
    local ends = {}
    for k in next, stuck.ends or {} do ends[#ends + 1] = k end
    table.sort(ends)
    rec("stall", line_of(stuck.id), "stalled at step " .. stuck.id .. ": it never ended under the fake host (ends on: "
        .. (#ends > 0 and table.concat(ends, ", ") or "nothing") .. ")")
elseif not failed and completes == 0 then
    rec("stall", 0, "walked every step but the mission never completed"
        .. (mode == "cutscene_never_loads" and #(c.outro or {}) > 0
            and "; with the outro cutscene never loading, no guard timer completed it" or ""))
elseif completes > 1 then
    rec("problem", 0, "the mission completed " .. completes .. " times")
end
rec("summary", 0, "COMPLETES " .. completes .. " (outro " .. #(c.outro or {}) .. ", mode " .. mode .. ")")
rec("summary", 0, stuck and ("STUCK at " .. stuck.id) or failed and "STOPPED BY AN ERROR" or "WALKED ALL STEPS")
return finish()
