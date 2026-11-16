-- The testbed's script. Walking up to the device door opens it, and the open door sends the
-- player to the interior region.
local missions = require("missions")
local mission = require(missions.EDZ_FREEROAM)

-- Set once, so a reload or a region change does not arm the trigger again.
local ARMED = "testbed.armed"
local THROUGH = "testbed.through"
-- The door's position channel takes 10 seconds for its full range.
local DOOR_OPEN_MS = 11000
-- The interior: region and one of its spawn sets, read from the Current Status overlay.
local INTERIOR_REGION = 416
local INTERIOR_SPAWN = 0x2EA8FB98

local function door(context, open)
    context:slot(mission.Slot.D_LURE):set_channel{
        channel = context.sdk.device_channels.position,
        value = context.sdk.unit(open and 1 or 0),
        snap = false,
    }
end

local function start(context, state)
    -- Only the private instance can drive a device or arm a trigger.
    if context.activity_role ~= "private" or state:variable(ARMED) ~= nil then return end
    context:set_variable(ARMED, true)
    door(context, false)
    context:slot(mission.Slot.PT_LEFT_TOWN):fire_trigger()
end

return {
    on_start = start,
    on_load = start,
    on_event_client_state_changed = start,
    on_event_player_trigger = function(context, state, event)
        if context.activity_role ~= "private" or state:variable(THROUGH) ~= nil then return end
        context:set_variable(THROUGH, true)
        door(context, true)
        context:start_timer(THROUGH, DOOR_OPEN_MS)
    end,
    on_event_timer_elapsed = function(context, state, event)
        if event.timer_name == THROUGH then
            context:teleport{ region = INTERIOR_REGION, spawn_set_hash = INTERIOR_SPAWN }
        end
    end,
}
