using System.Collections.Generic;
using UnityEngine;
using AjisaiFlow.UnityAgent.Editor.Tools;

namespace AjisaiFlow.UnityAgent.Editor.MeshPaint
{
    /// <summary>
    /// A single Renderer+slot's paint context: the underlying preview session,
    /// the list of staged ops, and replay helpers.
    ///
    /// Replay model:
    ///   preview = BakedOrigin → op[0] → op[1] → … → op[N-1] → draftOpForCurrentTab
    ///
    /// BakedOrigin lives on the <see cref="MeshPaintPreviewSession"/> and is only
    /// rewritten by a destructive Commit. Ops are additive-only during a session;
    /// users remove individual entries through the changes-list UI, which triggers
    /// a full replay from BakedOrigin.
    /// </summary>
    internal class MeshPaintSessionEntry
    {
        public readonly MeshPaintPreviewSession Session = new MeshPaintPreviewSession();
        public readonly List<MeshPaintOperation> Ops = new List<MeshPaintOperation>();
        public Renderer Renderer;
        public int MaterialSlot;

        /// <summary>
        /// True when the preview / op list has changed since the last successful
        /// commit (or since session start, if never committed). Drives "すべて適用"
        /// — it must re-write the PNG even when Ops.Count drops to zero so that
        /// removing all ops after a commit actually undoes the on-disk change.
        /// </summary>
        public bool IsDirtyForCommit { get; private set; }

        public void MarkDirtyForCommit() => IsDirtyForCommit = true;
        public void MarkCleanForCommit() => IsDirtyForCommit = false;

        public bool IsStarted => Session.IsActive;

        public bool Begin(Renderer r, GameObject avatarRoot, int slot)
        {
            if (Session.IsActive) return true;
            bool ok = Session.Begin(r, avatarRoot, slot);
            if (ok)
            {
                Renderer = r;
                MaterialSlot = slot;
            }
            return ok;
        }

        /// <summary>
        /// Recompute preview = BakedOrigin + every committed op. The current
        /// tab's draft op is NOT included — the caller layers that on top
        /// after this returns.
        /// </summary>
        public void ReplayAll()
        {
            if (!Session.IsActive) return;
            var pixels = ComputePreOpsTail();
            Session.SetBaseline(pixels);
        }

        /// <summary>
        /// Pixels representing "BakedOrigin with every committed op applied, in order".
        /// This is what a draft op should be layered onto.
        /// </summary>
        public Color[] ComputePreOpsTail()
        {
            if (!Session.IsActive || Session.BakedOrigin == null)
                return null;

            Color[] pixels = (Color[])Session.BakedOrigin.Clone();
            foreach (var op in Ops)
            {
                if (op == null) continue;
                pixels = MeshPaintOpApplier.Apply(
                    pixels, op,
                    Session.Width, Session.Height,
                    Session.CachedMesh, Session.CachedIslands, Session.CachedIslandGroups);
            }
            return pixels;
        }

        public void AddOp(MeshPaintOperation op)
        {
            if (op == null || op.IsNoop()) return;
            Ops.Add(op);
            IsDirtyForCommit = true;
            ReplayAll();
        }

        public bool RemoveOpAt(int index)
        {
            if (index < 0 || index >= Ops.Count) return false;
            Ops.RemoveAt(index);
            IsDirtyForCommit = true;
            ReplayAll();
            return true;
        }

        public void ClearOps()
        {
            if (Ops.Count == 0) return;
            Ops.Clear();
            IsDirtyForCommit = true;
            ReplayAll();
        }

        public void Dispose()
        {
            if (Session.IsActive)
                Session.End(autoCommit: false);
            Ops.Clear();
        }
    }
}
