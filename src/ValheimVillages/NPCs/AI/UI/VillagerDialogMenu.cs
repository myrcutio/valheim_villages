using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Abilities;
using ValheimVillages.NPCs;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Dialog menu for interacting with villager NPCs.
    /// Styled to match Valheim's native UI aesthetic (AzuMatt/Jötunn style).
    /// 
    /// DEPRECATED: For NPC types with virtual stations (Farmer, TavernKeeper),
    /// the crafting UI with tabs (VillagerTabManager, InfoTab, DebugTab) is used
    /// instead. This class remains as a fallback for NPC types without virtual
    /// stations (Guard, Mountaineer, Scout, Trader, etc.).
    /// </summary>
    public class VillagerDialogMenu : MonoBehaviour
    {
        private static VillagerDialogMenu s_instance;
        
        private VillagerBehaviorBridge m_currentVillager;
        private string m_villagerName;
        private bool m_visible = false;
        private Rect m_windowRect;
        private Vector2 m_scrollPosition;
        private GUIStyle m_windowStyle;
        private GUIStyle m_headerStyle;
        private GUIStyle m_headerShadowStyle;
        private GUIStyle m_labelStyle;
        private GUIStyle m_buttonStyle;
        private GUIStyle m_boxStyle;
        private GUIStyle m_dividerStyle;
        private GUIStyle m_sectionTitleStyle;
        private bool m_stylesInitialized = false;
        private bool m_assetsLoaded = false;

        // Valheim's official colors (from Jötunn GUIManager source)
        private static readonly Color ValheimOrange = new Color(1f, 0.631f, 0.235f, 1f);
        private static readonly Color ValheimBeige = new Color(0.8529f, 0.725f, 0.5331f, 1f);
        private static readonly Color ValheimYellow = new Color(1f, 0.889f, 0f, 1f);
        private static readonly Color TextShadowColor = new Color(0f, 0f, 0f, 0.8f);
        
        // Button color state for active/pressed (from Jötunn)
        private static readonly Color ButtonActive = new Color(0.537f, 0.556f, 0.556f, 1f);

        // Cached Valheim assets
        private Font m_valheimFont;
        private Texture2D m_woodPanelTexture;
        private Texture2D m_buttonTexture;
        private Texture2D m_buttonHoverTexture;
        private Texture2D m_boxTexture;

        // Cache for top locations
        private List<KnownLocation> m_topLocations = new();
        private float m_lastRefreshTime;
        private const float RefreshInterval = 1f;

        // Window dimensions
        private const float WindowWidth = 400f;
        private const float WindowHeight = 650f;

        /// <summary>
        /// Show the dialog menu for a villager.
        /// </summary>
        public static void Show(VillagerBehaviorBridge villager, string name)
        {
            if (villager == null) return;

            // Destroy any stale instances from previous assembly loads
            CleanupStaleInstances();

            // Create fresh instance each time (supports hot reload)
            if (s_instance == null)
            {
                var go = new GameObject("VillagerDialogMenu");
                s_instance = go.AddComponent<VillagerDialogMenu>();
                // Note: No DontDestroyOnLoad - we want fresh instances after hot reload
            }

            s_instance.m_currentVillager = villager;
            s_instance.m_villagerName = name;
            s_instance.m_visible = true;
            s_instance.CenterWindow();
            s_instance.RefreshLocations();
            
            // Pause the villager while menu is open
            villager.SetPaused(true);
            
            // Enable cursor mode for UI interaction
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            
            // Reset input axes to prevent accumulated mouse movement from affecting camera
            Input.ResetInputAxes();
            
            Plugin.Log?.LogInfo($"Opened dialog for villager: {name}");
        }

        /// <summary>
        /// Clean up any stale dialog menu instances from previous assembly loads.
        /// </summary>
        private static void CleanupStaleInstances()
        {
            // Find and destroy any existing VillagerDialogMenu GameObjects
            // This handles cases where hot reload leaves orphaned objects
#pragma warning disable CS0618 // FindObjectsOfType is deprecated but FindObjectsByType may not exist in this Unity version
            var existingMenus = Object.FindObjectsOfType<VillagerDialogMenu>();
            foreach (var menu in existingMenus)
            {
                if (menu != s_instance)
                {
                    Plugin.Log?.LogInfo("Destroying stale VillagerDialogMenu instance");
                    Destroy(menu.gameObject);
                }
            }
            
            // Also check for orphaned GameObjects by name (from old assembly versions)
            var orphanedGOs = Object.FindObjectsOfType<GameObject>()
                .Where(go => go.name == "VillagerDialogMenu" && go.GetComponent<VillagerDialogMenu>() == null);
#pragma warning restore CS0618
            foreach (var go in orphanedGOs)
            {
                Plugin.Log?.LogInfo("Destroying orphaned VillagerDialogMenu GameObject");
                Destroy(go);
            }
        }

        /// <summary>
        /// Hide the dialog menu.
        /// </summary>
        public static void Hide()
        {
            // Restore cursor state first regardless of instance state
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            
            if (s_instance == null) return;

            if (s_instance.m_currentVillager != null)
            {
                s_instance.m_currentVillager.SetPaused(false);
            }

            s_instance.m_currentVillager = null;
            s_instance.m_villagerName = null;
            s_instance.m_visible = false;
            
            Plugin.Log?.LogInfo("Closed villager dialog");
        }

        /// <summary>
        /// Check if dialog is currently open.
        /// </summary>
        public static bool IsVisible => s_instance != null && s_instance.m_visible;

        /// <summary>
        /// Clean up when the object is destroyed (e.g., scene change).
        /// </summary>
        private void OnDestroy()
        {
            if (s_instance == this)
            {
                // Ensure cursor is restored if we're destroyed while visible
                if (m_visible)
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                    
                    if (m_currentVillager != null)
                    {
                        m_currentVillager.SetPaused(false);
                    }
                }
                s_instance = null;
            }
        }

        private void CenterWindow()
        {
            m_windowRect = new Rect(
                (Screen.width - WindowWidth) / 2,
                (Screen.height - WindowHeight) / 2,
                WindowWidth,
                WindowHeight
            );
        }

        #region Asset Loading

        /// <summary>
        /// Load Valheim's native UI assets (fonts, textures).
        /// </summary>
        private void LoadValheimAssets()
        {
            if (m_assetsLoaded) return;

            try
            {
                // Load Valheim's font
                m_valheimFont = LoadValheimFont();
                
                // Note: Skip atlas texture loading - extracting sprites from Unity's 
                // packed atlases is unreliable for IMGUI. We use custom-generated 
                // textures that match Valheim's color scheme instead.

                if (m_valheimFont != null)
                {
                    Plugin.Log?.LogInfo($"Loaded Valheim font: {m_valheimFont.name}");
                }
                else
                {
                    Plugin.Log?.LogWarning("Could not load Valheim font, using fallback");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"Failed to load Valheim assets: {ex.Message}");
            }

            m_assetsLoaded = true;
        }

        /// <summary>
        /// Load Valheim's AveriaSerifBold font from game resources.
        /// </summary>
        private Font LoadValheimFont()
        {
            // Try to find the font in loaded resources
            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            
            // Look for AveriaSerifLibre-Bold (Valheim's main UI font)
            var valheimFont = fonts.FirstOrDefault(f => 
                f.name.Contains("AveriaSerifLibre-Bold") || 
                f.name.Contains("AveriaSansLibre-Bold"));
            
            if (valheimFont != null)
            {
                return valheimFont;
            }

            // Fallback: try to find any Averia font
            valheimFont = fonts.FirstOrDefault(f => f.name.Contains("Averia"));
            
            return valheimFont;
        }

        /// <summary>
        /// Create a simple colored texture.
        /// </summary>
        private Texture2D CreateColorTexture(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Create a wood-panel style texture with border and gradient.
        /// </summary>
        private Texture2D CreateWoodPanelTexture(int width, int height)
        {
            var tex = new Texture2D(width, height);
            var pixels = new Color[width * height];
            
            // Colors matching Valheim's dark wood aesthetic
            var bgDark = new Color(0.12f, 0.09f, 0.06f, 0.95f);
            var bgLight = new Color(0.18f, 0.14f, 0.10f, 0.95f);
            var borderOuter = new Color(0.08f, 0.06f, 0.04f, 1f);
            var borderInner = new Color(0.35f, 0.28f, 0.18f, 1f);
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color c;
                    
                    // Outer border (2px dark)
                    if (x < 2 || x >= width - 2 || y < 2 || y >= height - 2)
                    {
                        c = borderOuter;
                    }
                    // Inner border highlight (2px gold-ish)
                    else if (x < 4 || x >= width - 4 || y < 4 || y >= height - 4)
                    {
                        c = borderInner;
                    }
                    // Background with subtle vertical gradient
                    else
                    {
                        float t = (float)y / height;
                        c = Color.Lerp(bgDark, bgLight, t * 0.3f);
                    }
                    
                    pixels[y * width + x] = c;
                }
            }
            
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Create a button texture with border.
        /// </summary>
        private Texture2D CreateButtonTexture(Color bgColor, Color borderColor)
        {
            int size = 16;
            var tex = new Texture2D(size, size);
            var pixels = new Color[size * size];
            
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool isBorder = x < 1 || x >= size - 1 || y < 1 || y >= size - 1;
                    pixels[y * size + x] = isBorder ? borderColor : bgColor;
                }
            }
            
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Create a subtle box texture for list items.
        /// </summary>
        private Texture2D CreateBoxTexture()
        {
            int size = 8;
            var tex = new Texture2D(size, size);
            var pixels = new Color[size * size];
            
            var bg = new Color(0.08f, 0.06f, 0.04f, 0.6f);
            var border = new Color(0.25f, 0.2f, 0.15f, 0.4f);
            
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool isBorder = x < 1 || x >= size - 1 || y < 1 || y >= size - 1;
                    pixels[y * size + x] = isBorder ? border : bg;
                }
            }
            
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        #endregion

        #region Style Initialization

        private void InitStyles()
        {
            if (m_stylesInitialized) return;

            // Load Valheim assets first
            LoadValheimAssets();

            // Get font (Valheim's or fallback)
            Font uiFont = m_valheimFont ?? GUI.skin.font;

            // Create Valheim-styled textures
            m_woodPanelTexture = CreateWoodPanelTexture(64, 64);
            
            m_buttonTexture = CreateButtonTexture(
                new Color(0.20f, 0.16f, 0.12f, 0.9f),
                new Color(0.45f, 0.35f, 0.25f, 1f));
            
            m_buttonHoverTexture = CreateButtonTexture(
                new Color(0.28f, 0.22f, 0.16f, 0.95f),
                new Color(0.6f, 0.48f, 0.32f, 1f));
            
            m_boxTexture = CreateBoxTexture();

            // Window style - wood panel background
            m_windowStyle = new GUIStyle(GUI.skin.window)
            {
                padding = new RectOffset(20, 20, 45, 20),
                border = new RectOffset(12, 12, 12, 12),
                font = uiFont,
                fontSize = 18
            };
            m_windowStyle.normal.background = m_woodPanelTexture;
            m_windowStyle.normal.textColor = ValheimOrange;
            m_windowStyle.onNormal.background = m_woodPanelTexture;

            // Header style - larger text for villager name (main text)
            m_headerStyle = new GUIStyle(GUI.skin.label)
            {
                font = uiFont,
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            m_headerStyle.normal.textColor = ValheimOrange;

            // Header shadow style (for outline effect)
            m_headerShadowStyle = new GUIStyle(m_headerStyle);
            m_headerShadowStyle.normal.textColor = TextShadowColor;

            // Section title style
            m_sectionTitleStyle = new GUIStyle(GUI.skin.label)
            {
                font = uiFont,
                fontSize = 16,
                fontStyle = FontStyle.Bold
            };
            m_sectionTitleStyle.normal.textColor = ValheimYellow;

            // Label style
            m_labelStyle = new GUIStyle(GUI.skin.label)
            {
                font = uiFont,
                fontSize = 14
            };
            m_labelStyle.normal.textColor = ValheimBeige;

            // Button style - Valheim themed
            m_buttonStyle = new GUIStyle(GUI.skin.button)
            {
                font = uiFont,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(12, 12, 8, 8),
                border = new RectOffset(4, 4, 4, 4),
                alignment = TextAnchor.MiddleCenter
            };
            m_buttonStyle.normal.background = m_buttonTexture;
            m_buttonStyle.normal.textColor = ValheimOrange;
            m_buttonStyle.hover.background = m_buttonHoverTexture;
            m_buttonStyle.hover.textColor = ValheimYellow;
            m_buttonStyle.active.background = m_buttonTexture;
            m_buttonStyle.active.textColor = ButtonActive;

            // Box style for location rows
            m_boxStyle = new GUIStyle(GUI.skin.box)
            {
                font = uiFont,
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 4),
                border = new RectOffset(2, 2, 2, 2)
            };
            m_boxStyle.normal.background = m_boxTexture;
            m_boxStyle.normal.textColor = ValheimBeige;

            // Divider style
            m_dividerStyle = new GUIStyle();
            m_dividerStyle.normal.background = CreateColorTexture(new Color(0.6f, 0.5f, 0.3f, 0.6f));

            m_stylesInitialized = true;
        }

        #endregion

        private void Update()
        {
            if (!m_visible) return;

            // Only unlock cursor if it's currently locked (avoid resetting cursor position)
            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
            }
            if (!Cursor.visible)
            {
                Cursor.visible = true;
            }

            // Close on Escape, Tab, or E key (like other Valheim menus)
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.E))
            {
                Hide();
                return;
            }

            // Periodic refresh of location data
            if (Time.time - m_lastRefreshTime > RefreshInterval)
            {
                RefreshLocations();
            }
        }

        private void OnGUI()
        {
            if (!m_visible || m_currentVillager == null) return;

            InitStyles();

            // Draw window (no background overlay to match other Valheim menus)
            GUI.depth = 0;
            GUI.BringWindowToFront(54321);
            m_windowRect = GUI.Window(54321, m_windowRect, DrawWindow, "", m_windowStyle);
        }

        private void DrawWindow(int windowId)
        {
            GUILayout.BeginVertical();

            // Villager name header (pinned at top, not scrollable)
            DrawTextWithShadow(m_villagerName, m_headerStyle, m_headerShadowStyle);
            GUILayout.Space(8);

            // Scrollable content area for everything between header and farewell button
            m_scrollPosition = GUILayout.BeginScrollView(m_scrollPosition, GUILayout.ExpandHeight(true));

            // Info section
            var memory = m_currentVillager.Memory;
            if (memory != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Known Places: {memory.KnownLocations.Count}", m_labelStyle);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"Comfort: {memory.BestComfortLevel:F0}", m_labelStyle);
                GUILayout.EndHorizontal();
                GUILayout.Space(8);
            }

            // NPC ability section (type-specific)
            DrawAbilitySection();

            // Divider line
            GUILayout.Box("", m_dividerStyle, GUILayout.ExpandWidth(true), GUILayout.Height(2));
            GUILayout.Space(12);

            // Section title with Valheim yellow
            GUILayout.Label("Favorite Places", m_sectionTitleStyle);
            GUILayout.Space(8);

            if (m_topLocations.Count == 0)
            {
                GUILayout.Space(10);
                GUILayout.Label("Still exploring...", m_sectionTitleStyle);
                GUILayout.Space(5);
                GUILayout.Label("This villager hasn't found any favorite spots yet.", m_labelStyle);
            }
            else
            {
                foreach (var loc in m_topLocations)
                {
                    DrawLocationRow(loc);
                }
            }

            // Debug section
            GUILayout.Space(8);
            GUILayout.Box("", m_dividerStyle, GUILayout.ExpandWidth(true), GUILayout.Height(2));
            GUILayout.Space(8);
            GUILayout.Label("Debug Commands", m_sectionTitleStyle);
            GUILayout.Space(4);

            // Debug info
            string stateInfo = $"State: {m_currentVillager.CurrentState}";
            if (m_currentVillager.CurrentTarget.HasValue)
            {
                float targetDist = Vector3.Distance(m_currentVillager.transform.position, m_currentVillager.CurrentTarget.Value);
                stateInfo += $" | Target: {targetDist:F0}m away";
            }
            int variety = m_currentVillager.Memory?.GetLocationTypeVariety() ?? 0;
            stateInfo += $" | Variety: {variety} types";
            GUILayout.Label(stateInfo, m_labelStyle);
            GUILayout.Space(4);

            // Debug buttons row 1
            GUILayout.BeginHorizontal();
            
            if (GUILayout.Button("Run Move Tests", m_buttonStyle, GUILayout.Height(32)))
            {
                if (m_currentVillager.IsTestRunning)
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, 
                        "Tests already running...");
                }
                else if (m_currentVillager.RunMovementTests())
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, 
                        "Starting movement tests (~17 seconds)...");
                }
                else
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, 
                        "Could not start movement tests");
                }
                Hide();
            }
            
            GUILayout.Space(8);
            
            if (GUILayout.Button("Go to Bed", m_buttonStyle, GUILayout.Height(32)))
            {
                var target = m_currentVillager.DebugWanderToLocationType(LocationType.Bed);
                if (target.HasValue)
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "Villager going to bed");
                }
                Hide();
            }
            
            GUILayout.EndHorizontal();
            
            // Debug buttons row 2
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            
            if (GUILayout.Button("Find Fire", m_buttonStyle, GUILayout.Height(32)))
            {
                var target = m_currentVillager.DebugWanderToLocationType(LocationType.Fire);
                if (target.HasValue)
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "Villager going to fire");
                }
                else
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "No fire location known");
                }
                Hide();
            }
            
            GUILayout.Space(8);
            
            if (GUILayout.Button("Find Chair", m_buttonStyle, GUILayout.Height(32)))
            {
                var target = m_currentVillager.DebugWanderToLocationType(LocationType.Chair);
                if (target.HasValue)
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "Villager going to chair");
                }
                else
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "No chair location known");
                }
                Hide();
            }
            
            GUILayout.EndHorizontal();

            // Map pin button
            if (m_topLocations.Count > 0)
            {
                GUILayout.Space(8);
                if (GUILayout.Button("Mark All on Map", m_buttonStyle, GUILayout.Height(38)))
                {
                    foreach (var loc in m_topLocations)
                    {
                        AddMapPin(loc);
                    }
                }
            }

            GUILayout.EndScrollView();

            // Farewell button pinned at bottom (always visible)
            GUILayout.Space(8);
            if (GUILayout.Button("Farewell", m_buttonStyle, GUILayout.Height(38)))
            {
                Hide();
            }

            GUILayout.EndVertical();

            // Make window draggable from title area
            GUI.DragWindow(new Rect(0, 0, m_windowRect.width, 45));
        }

        /// <summary>
        /// Draw guard-specific section showing patrol status and breach alerts.
        /// </summary>
        private void DrawGuardSection()
        {
            if (m_currentVillager == null) return;
            if (m_currentVillager.NpcType != NPCs.NpcType.Guard) return;

            var ai = m_currentVillager.AI;
            if (ai == null || ai.GuardBehavior == null) return;

            var guard = ai.GuardBehavior;

            GUILayout.Box("", m_dividerStyle, GUILayout.ExpandWidth(true), GUILayout.Height(2));
            GUILayout.Space(8);
            GUILayout.Label("Guard Duty", m_sectionTitleStyle);
            GUILayout.Space(4);

            if (!guard.IsDiscoveryComplete)
            {
                string phase = ai.CurrentState == BehaviorState.Scouting
                    ? "Scouting the perimeter..."
                    : "Mapping village boundary...";
                GUILayout.Label(phase, m_labelStyle);
            }
            else if (guard.IsAlarmed)
            {
                // Breach alert with warning style
                var warningStyle = new GUIStyle(m_labelStyle);
                warningStyle.normal.textColor = new Color(1f, 0.3f, 0.2f, 1f);

                GUILayout.Label("A breach has been detected in the village walls!", warningStyle);
                GUILayout.Space(8);

                if (GUILayout.Button("Show me the breach", m_buttonStyle, GUILayout.Height(36)))
                {
                    guard.WalkToBreach();
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                        "The guard will walk to the breach location.");
                    Hide();
                }

                GUILayout.Space(4);
                GUILayout.Label("Repair the wall gap, then the guard will resume patrol.", m_labelStyle);
            }
            else
            {
                GUILayout.Label(
                    $"Patrolling the village perimeter ({guard.WaypointCount} waypoints).",
                    m_labelStyle);
                GUILayout.Label("The village is secure. No breaches detected.", m_labelStyle);
            }

            GUILayout.Space(8);
        }

        /// <summary>
        /// Draw ability section for NPC types that teach the player techniques.
        /// </summary>
        private void DrawAbilitySection()
        {
            if (m_currentVillager == null) return;

            // Draw guard section if applicable
            DrawGuardSection();

            var npcType = m_currentVillager.NpcType;
            if (npcType != NPCs.NpcType.Mountaineer) return;

            GUILayout.Box("", m_dividerStyle, GUILayout.ExpandWidth(true), GUILayout.Height(2));
            GUILayout.Space(8);
            GUILayout.Label("Technique", m_sectionTitleStyle);
            GUILayout.Space(4);

            bool learned = VillagerAbilityManager.HasLearnedMountainStride();

            if (!learned)
            {
                GUILayout.Label(
                    "The Mountaineer can teach you to traverse steep terrain without sliding.",
                    m_labelStyle);
                GUILayout.Space(4);

                if (GUILayout.Button("Learn Mountain Stride", m_buttonStyle, GUILayout.Height(36)))
                {
                    VillagerAbilityManager.LearnMountainStride();
                }
            }
            else
            {
                bool active = VillagerAbilityManager.IsActive();
                float cooldown = VillagerAbilityManager.GetCooldownRemaining();

                if (active)
                {
                    GUILayout.Label("Mountain Stride is active -- you won't slide on slopes.", m_labelStyle);
                }
                else if (cooldown > 0f)
                {
                    int minutes = UnityEngine.Mathf.CeilToInt(cooldown / 60f);
                    GUILayout.Label(
                        $"Mountain Stride (learned) -- ready in {minutes}m. Press R to activate.",
                        m_labelStyle);
                }
                else
                {
                    GUILayout.Label(
                        "Mountain Stride (learned) -- Press R to activate (5 min buff).",
                        m_labelStyle);
                }
            }

            GUILayout.Space(8);
        }

        /// <summary>
        /// Draw text with a shadow/outline effect for better readability.
        /// </summary>
        private void DrawTextWithShadow(string text, GUIStyle mainStyle, GUIStyle shadowStyle)
        {
            var rect = GUILayoutUtility.GetRect(new GUIContent(text), mainStyle);
            
            // Draw shadow (offset by 1-2 pixels)
            var shadowRect = new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height);
            GUI.Label(shadowRect, text, shadowStyle);
            
            // Draw main text
            GUI.Label(rect, text, mainStyle);
        }

        private void DrawLocationRow(KnownLocation loc)
        {
            GUILayout.BeginHorizontal(m_boxStyle);

            // Location description with icon
            string icon = GetLocationIcon(loc.Type);
            string description = GetLocationDescription(loc);
            string shelterText = loc.HasShelter ? " (sheltered)" : "";
            
            // Main location text
            GUILayout.Label($"{icon} {description}{shelterText}", m_labelStyle, GUILayout.ExpandWidth(true));
            
            // Distance from villager (in muted color)
            if (m_currentVillager != null)
            {
                float dist = Vector3.Distance(m_currentVillager.transform.position, loc.Position);
                var distStyle = new GUIStyle(m_labelStyle);
                distStyle.normal.textColor = new Color(0.7f, 0.65f, 0.5f, 0.9f);
                GUILayout.Label($"{dist:F0}m", distStyle, GUILayout.Width(50));
            }

            // Pin button (smaller for row)
            if (GUILayout.Button("Mark", m_buttonStyle, GUILayout.Width(60), GUILayout.Height(28)))
            {
                AddMapPin(loc);
            }

            GUILayout.EndHorizontal();
        }

        private void RefreshLocations()
        {
            m_lastRefreshTime = Time.time;
            m_topLocations.Clear();

            if (m_currentVillager?.Memory == null) return;

            var memory = m_currentVillager.Memory;
            
            // Get all locations and score them
            var scoredLocations = memory.KnownLocations
                .Select(loc => new { Location = loc, Score = ScoreLocationForDisplay(loc) })
                .OrderByDescending(x => x.Score)
                .Take(5)
                .Select(x => x.Location)
                .ToList();

            m_topLocations = scoredLocations;
        }

        private float ScoreLocationForDisplay(KnownLocation loc)
        {
            float score = 0f;

            switch (loc.Type)
            {
                case LocationType.Bed: score += 100f; break;
                case LocationType.Fire: score += 50f; break;
                case LocationType.Chair: score += 40f; break;
                case LocationType.Table: score += 35f; break;
                case LocationType.Farm: score += 30f; break;
                case LocationType.Animals: score += 30f; break;
                case LocationType.Shelter: score += 10f; break;
                case LocationType.Patrol: score += 5f; break;
            }

            if (loc.HasShelter) score += 15f;
            score += loc.ComfortValue * 10f;

            return score;
        }

        #region Location Display

        private string GetLocationIcon(LocationType type)
        {
            // Simple text icons that work with default fonts
            return type switch
            {
                LocationType.Bed => "[Bed]",
                LocationType.Fire => "[Fire]",
                LocationType.Chair => "[Seat]",
                LocationType.Table => "[Table]",
                LocationType.Shelter => "[Roof]",
                LocationType.Farm => "[Field]",
                LocationType.Animals => "[Beasts]",
                LocationType.Patrol => "[Path]",
                _ => "[?]"
            };
        }

        private string GetLocationDescription(KnownLocation loc)
        {
            return loc.Type switch
            {
                LocationType.Bed => "A cozy bed",
                LocationType.Fire => loc.HasShelter ? "A warm hearth" : "A campfire",
                LocationType.Chair => "A comfortable seat",
                LocationType.Table => "A gathering table",
                LocationType.Shelter => "A dry spot",
                LocationType.Farm => "Open fields",
                LocationType.Animals => "Friendly creatures",
                LocationType.Patrol => "A scenic path",
                _ => "An interesting spot"
            };
        }

        #endregion

        #region Map Pins

        private void AddMapPin(KnownLocation loc)
        {
            var minimap = Minimap.instance;
            if (minimap == null)
            {
                Plugin.Log?.LogWarning("Cannot add pin: Minimap not available");
                Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, 
                    "Cannot mark: Map not available");
                return;
            }

            string description = GetLocationDescription(loc);
            
            try
            {
                // Use reflection to call AddPin with the correct signature
                var addPinMethod = typeof(Minimap).GetMethod("AddPin", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null,
                    new System.Type[] { typeof(Vector3), typeof(Minimap.PinType), typeof(string), typeof(bool), typeof(bool) },
                    null);

                if (addPinMethod != null)
                {
                    addPinMethod.Invoke(minimap, new object[] { 
                        loc.Position, 
                        Minimap.PinType.Icon3, 
                        $"{m_villagerName}: {description}", 
                        true, 
                        false 
                    });
                    
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, 
                        $"Marked: {description}");
                    Plugin.Log?.LogInfo($"Added map pin at {loc.Position}: {description}");
                }
                else
                {
                    string coords = $"({loc.Position.x:F0}, {loc.Position.z:F0})";
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center, 
                        $"{description} at {coords}");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogError($"Failed to add map pin: {ex.Message}");
                
                string coords = $"({loc.Position.x:F0}, {loc.Position.z:F0})";
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, 
                    $"{description} at {coords}");
            }
        }

        #endregion
    }
}
