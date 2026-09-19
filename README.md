<img src="https://raw.githubusercontent.com/myrcutio/valheim_villages/main/Thunderstore/icon.png" alt="Valheim Villages" width="200">

# Valheim Villages

Adds villagers who map your walled village, find your stations and chests, and keep work
orders stocked from what you have. They farm, cook, smelt, haul, repair and patrol.

> **Early access.** Expect rough edges — see Known Issues.

### Install

Requires [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/). Install via a mod manager (r2modman / Thunderstore Mod Manager) by searching for **ValheimVillages**, or manually drop `ValheimVillages.dll` into `BepInEx/plugins/ValheimVillages/`. For dedicated servers, install the same plugin on the server.

### Current features

  - villagers
    - when idle they look for work: orders left in chests, or items lying on the ground
      - loose items are swept into chests — currently whichever is nearest, which isn't always sensible
      - crops are harvested and replanted
      - farmers forage ripe berries, mushrooms and thistle growing inside the village
      - coal refined in a kiln, ore smelted in a smelter, meat roasted on a cooking station
    - they find their way through doors, up stairs you built, across walls and ramparts, and over terrain you've reshaped
    - they can fight, badly — guards shoot crossbows, everyone else runs and cowers
  - multiple villages
    - each village has its own villagers, work orders and territory, and runs on its own
  - village registry station
    - marks out a walled-in area as a village
    - recruit new villagers, revive dead ones, and see what everyone is up to
  - work orders
    - any recipe can be turned into an "order"
    - right-click one to set how much you want kept in stock
    - its icon shows a green check when stocked, a red X when materials are missing
    - an order belongs to the village its token is kept in — move the token to a chest in
      another village and the order goes with it
  - village map
    - your walls decide where the village ends, and the area is drawn on a map in the Tasks menu
  - interface
    - extra tabs on the registry station, with controller and Steam Deck navigation

### Screenshots

**Order anything you can craft.** Every vanilla station gains an **Order** button beside
Craft — turning that recipe into a standing work order the village keeps stocked.

![Order button on the cauldron's craft panel](https://raw.githubusercontent.com/myrcutio/valheim_villages/main/public/imgs/orders_ui.png)

**Or order from the villager.** Each villager's Orders tab lists everything their role can
make or gather, with the usual item card and ingredient list.

![A farmer's Orders tab listing craftable and foraged items](https://raw.githubusercontent.com/myrcutio/valheim_villages/main/public/imgs/villager-workorder-menu.png)

**Set the stock levels.** An order holds a minimum and maximum quantity; the villager tops
it up when it falls below the minimum and stops at the maximum. The panel shows the current
count, warns when ingredients have run out, and is bound to the chest holding the order token.

![Work order panel with min/max quota sliders and an out-of-materials warning](https://raw.githubusercontent.com/myrcutio/valheim_villages/main/public/imgs/workorder-settings.png)

**See what they're doing.** The Tasks tab reports the current activity and state, any
complaint about missing materials, and draws the village the villager has mapped out —
with their position and where they're headed.

![Tasks tab showing current activity, state and the village map](https://raw.githubusercontent.com/myrcutio/valheim_villages/main/public/imgs/task-details.png)

### Known Issues
If you are looking here you probably noticed something dreadfully wrong and broken, and for that I am sorry.
  - multiple villages not tested with two villages closer than about 200m
  - the villagers use dverger prefabs for animations, and sometimes a race condition on load fails to clean up the old prefab while still creating a new one, leading to very confused dwarves wandering your village
  - navmeshes are really hard
    - there are many, many edge cases that the villagers may not be able to properly navigate.  in particular this is a problem with shallow stairs and low gaps between floors.  to best avoid these, use wide stairs and ramps, and lots of clearance between floors and terrain.  if it's not handicap accessible, it will probably prove a problem for a villager.
    - gaps in village walls may break navmesh baking, particularly around doors on top of terrain (doors on pieces and floors work well)
  - villager rescue quests and map fragments may appear in the world, but they are very experimental and untested
  - villagers sometimes get distracted and let the meat burn on the cooking station.  mea culpa.
  - melee combat is absolutely not a thing and even a lowly greyling can end up wiping out a village if there isn't a capable guard or player to protect them
  - The village _should_ keep humming along while the player is away, but things may stall if the player loads in far away and hasn't loaded in that tile yet.  YMMV.
