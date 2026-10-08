using UnityEditor;
using UnityEngine;

namespace Kinema.MotionMatching.Editor
{
    /// <summary>
    /// The AI tab: every NPC's brain and current goal in one place, and a way to override them by
    /// hand. Motion matching proves the controller is input-agnostic; this proves the layer above it
    /// is brain-agnostic - a scripted rule set and a language model appear here identically, and you
    /// can push a manual command onto any agent regardless of which is driving it.
    ///
    /// The whole point of the dashboard requirement: what the AI is doing, and why, is visible and
    /// controllable from the window rather than buried in the scene.
    /// </summary>
    public sealed class AIDirectorTabDrawer
    {
        #region Private and Protected

        private Vector2 _scroll;
        private AIGoal _manualGoal = AIGoal.Wander;

        #endregion

        #region Main API

        public void Draw()
        {
            if (!Application.isPlaying)
            {
                MotionMatchingStyles.HelpRow("Enter Play mode to see and direct the AI agents. Add an AICommandProvider plus a brain (ScriptedAIBrain or LLMAIBrain) to any character to make it an agent - the Sandbox scene ships a crowd of them.", MessageType.Info);
                return;
            }

            AICommandProvider[] agents = UnityCompat.FindAll<AICommandProvider>();
            if (agents.Length == 0)
            {
                MotionMatchingStyles.HelpRow("No AI agents in the scene. An agent is a character with an AICommandProvider and an IAIBrain. Build the Sandbox scene (Tools > Kinema > Scenes > Sandbox) for a ready crowd.", MessageType.Info);
                return;
            }

            DrawSummary(agents);
            DrawAgentList(agents);
        }

        #endregion

        #region Tools and Utilities

        private static void DrawSummary(AICommandProvider[] agents)
        {
            int llm = 0, scripted = 0, moving = 0, avoiding = 0;
            foreach (var a in agents)
            {
                if (a.GetComponent("LLMAIBrain") != null) llm++;
                else if (a.GetComponent<ScriptedAIBrain>() != null) scripted++;
                if (a.Command.Goal != AIGoal.Idle && !a.ReachedGoal) moving++;
                if (a.Avoiding) avoiding++;
            }

            using (MotionMatchingStyles.BeginSection($"AI Agents — {agents.Length}"))
            using (new EditorGUILayout.HorizontalScope())
            {
                MotionMatchingStyles.StatCard(agents.Length.ToString(), "Agents", MotionMatchingStyles.Accent);
                MotionMatchingStyles.StatCard(scripted.ToString(), "Scripted", MotionMatchingStyles.Ok);
                MotionMatchingStyles.StatCard(llm.ToString(), "LLM-driven", llm > 0 ? MotionMatchingStyles.Ok : MotionMatchingStyles.Muted);
                MotionMatchingStyles.StatCard(moving.ToString(), "Moving", MotionMatchingStyles.Accent);
                MotionMatchingStyles.StatCard(avoiding.ToString(), "Steering", avoiding > 0 ? MotionMatchingStyles.Accent : MotionMatchingStyles.Muted);
            }
        }

        private void DrawAgentList(AICommandProvider[] agents)
        {
            using (MotionMatchingStyles.BeginSection("Agents"))
            {
                // Manual override picker: choose a goal, then push it onto an agent from its row.
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Manual command", MotionMatchingStyles.KeyLabel, GUILayout.Width(120));
                    _manualGoal = (AIGoal)EditorGUILayout.EnumPopup(_manualGoal);
                    if (GUILayout.Button("Send to all", GUILayout.Width(90)))
                        foreach (var a in agents) SendManual(a, _manualGoal);
                }
                MotionMatchingStyles.HelpRow("Manual MoveTo aims at the scene origin; Follow/Flee target the player. A brain resumes control on its next think.", MessageType.None);

                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(320));
                foreach (var agent in agents)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        AIAgentCommand command = agent.Command;

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button(agent.name, EditorStyles.boldLabel, GUILayout.Width(150)))
                                EditorGUIUtility.PingObject(agent.gameObject);
                            GUILayout.FlexibleSpace();
                            if (agent.Avoiding)
                                MotionMatchingStyles.StatusPill("STEERING", MotionMatchingStyles.Accent);
                            MotionMatchingStyles.StatusPill(BrainName(agent),
                                agent.GetComponent("LLMAIBrain") != null ? MotionMatchingStyles.Accent : MotionMatchingStyles.Ok);
                        }

                        MotionMatchingStyles.KeyValue("Goal", $"{command.Goal}{(command.SpeedScale > 0 ? $"  @ {command.SpeedScale:F1}x" : "")}");
                        MotionMatchingStyles.KeyValue("Status", agent.Status);
                        if (!string.IsNullOrEmpty(command.Reason))
                            MotionMatchingStyles.KeyValue("Reason", command.Reason);

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("MoveTo origin", EditorStyles.miniButtonLeft)) SendManual(agent, AIGoal.MoveTo);
                            if (GUILayout.Button("Follow player", EditorStyles.miniButtonMid)) SendManual(agent, AIGoal.Follow);
                            if (GUILayout.Button("Flee", EditorStyles.miniButtonMid)) SendManual(agent, AIGoal.Flee);
                            if (GUILayout.Button("Idle", EditorStyles.miniButtonRight)) SendManual(agent, AIGoal.Idle);
                        }
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

        /// <summary>
        /// Hands the agent a one-off command through its runtime override slot. The agent's own brain
        /// keeps running underneath and takes control again when the override lapses - a nudge, not a
        /// permanent rewire.
        /// </summary>
        private static void SendManual(AICommandProvider agent, AIGoal goal)
        {
            Transform player = FindPlayer(agent);
            agent.OverrideCommand(goal switch
            {
                AIGoal.MoveTo => AIAgentCommand.MoveTo(Vector3.zero, 1f, "move to origin"),
                AIGoal.Follow => AIAgentCommand.FollowTarget(player, 1f, "follow player"),
                AIGoal.Flee => new AIAgentCommand { Goal = AIGoal.Flee, Target = player, SpeedScale = 1f, Reason = "flee player" },
                _ => new AIAgentCommand { Goal = AIGoal.Idle, Reason = "idle" }
            });
        }

        private static Transform FindPlayer(AICommandProvider agent)
        {
            foreach (var c in UnityCompat.FindAll<MotionMatchingController>())
                if (c.GetComponent<AICommandProvider>() == null) return c.transform;
            return null;
        }

        private static string BrainName(AICommandProvider agent)
        {
            if (agent.GetComponent("LLMAIBrain") != null) return "LLM";
            if (agent.GetComponent<ScriptedAIBrain>() != null) return "SCRIPTED";
            return "MANUAL";
        }

        #endregion
    }
}
