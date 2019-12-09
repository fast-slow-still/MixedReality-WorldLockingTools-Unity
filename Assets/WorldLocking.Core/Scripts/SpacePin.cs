﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Microsoft.MixedReality.WorldLocking.Core
{
    /// <summary>
    /// Component helper for pinning the world locked space at a single reference point.
    /// </summary>
    /// <remarks>
    /// This component captures the initial pose of its gameObject, and then a second pose. It then
    /// adds that pair to the WorldLocking Alignment Manager. The manager then negotiates between all
    /// such added pins, based on the current head pose, to generate a frame-to-frame mapping aligning
    /// the Frozen Space such that the pins match up as well as possible.
    /// Another way to phrase this is:
    ///    Given an arbitrary pose (the "modeling pose"),
    ///    and a pose aligned somehow to the real world (the "world locked pose"),
    ///    apply a correction to the camera such that a virtual object with coordinates of the modeling pose
    ///    will appear overlaid on the real world at the position and orientation described by the locked pose.
    /// For this component, the locked pose must come in via one of the following three APIs:
    ///     <see cref="SetFrozenPose(Pose)"/> with input pose in Frozen Space, which includes pinning.
    ///     <see cref="SetSpongyPose(Pose)"/> with input pose in Spongy Space, which is the space of the camera's parent,
    ///         and is the same space the camera moves in, and that native APIs return values in (e.g. XR).
    ///     <see cref="SetLockedPose(Pose)"/> with input pose in Locked Space, which is the space stabilized by
    ///         the Frozen World engine DLL but excluding pinning.
    /// Note that since the Frozen Space is shifted by the AlignmentManager, calling SetFrozenPose(p) with the same Pose p
    /// twice is probably an error, since the Pose p would refer to different a location after the first call.
    /// </remarks>
    public class SpacePin : MonoBehaviour
    {
        #region Private members

        /// <summary>
        /// manager dependency is set exactly once in Start().
        /// </summary>
        private WorldLockingManager manager = null;

        /// <summary>
        /// Read only access to manager dependency from derived classes.
        /// </summary>
        protected WorldLockingManager Manager => manager;

        /// <summary>
        /// Unique identifier for the alignment data from this instance.
        /// </summary>
        private ulong ulAnchorId = (ulong)AnchorId.Unknown;

        /// <summary>
        /// This wrapper for the anchorId is because the anchorId has to be stored
        /// as a ulong, which is the base class for the AnchorId enum. Unity only
        /// supports int-based enums, so will complain on serialization etc. for
        /// the ulong based AnchorId.
        /// </summary>
        private AnchorId AnchorId
        {
            get { return (AnchorId)ulAnchorId; }
            set { ulAnchorId = (ulong)value; }
        }

        /// <summary>
        /// Provide a unique anchor name. This is used for persistence.
        /// </summary>
        protected virtual string AnchorName { get { return gameObject.name + "SpacePin"; } }

        /// <summary>
        /// Whether this space pin is in active use pinning space
        /// </summary>
        protected bool PinActive { get { return AnchorId.IsKnown(); } }

        /// <summary>
        /// initialPose is Pose the gameObject is in at startup.
        /// </summary>
        private Pose initialPose = Pose.identity;

        /// <summary>
        /// Pose at startup.
        /// </summary>
        protected Pose InitialPose
        {
            get { return initialPose; }
        }

        /// <summary>
        /// First of the pair of poses submitted to alignment manager for alignment.
        /// </summary>
        protected virtual Pose ModelingPose
        {
            get { return initialPose; }
        }

        /// <summary>
        /// Second pose of pair submitted to alignment manager, always in Locked Space.
        /// </summary>
        private Pose lockedPose = Pose.identity;
        /// <summary>
        /// Accessor for world locked pose for derived classes.
        /// </summary>
        protected Pose LockedPose
        {
            get { return lockedPose; }
            set { lockedPose = value; }
        }

        /// <summary>
        /// Attachment point to react to refit operations.
        /// </summary>
        private IAttachmentPoint attachmentPoint = null;

        /// <summary>
        /// Id for fragment this pin belongs in.
        /// </summary>
        protected FragmentId FragmentId
        {
            get
            {
                if (attachmentPoint != null)
                {
                    return attachmentPoint.FragmentId;
                }
                return FragmentId.Unknown;
            }
        }

        #endregion Private members

        #region Unity members

        // Start is called before the first frame update
        protected virtual void Start()
        {
            /// Cache the WorldLockingManager as a dependency.
            manager = WorldLockingManager.GetInstance();

            /// Cache the initial pose.
            initialPose = transform.GetGlobalPose();

            /// Register for post-loaded messages from the Alignment Manager.
            /// When these come in check for the loading of the reference point
            /// associated with this pin. Reference is by unique name.
            manager.AlignmentManager.RegisterForLoad(RestoreOnLoad);
        }

        /// <summary>
        /// On destroy, unregister for the loaded event.
        /// </summary>
        protected virtual void OnDestroy()
        {
            manager.AlignmentManager.UnregisterForLoad(RestoreOnLoad);
        }

        #endregion Unity members

        #region Public APIs

        /// <summary>
        /// Transform pose to Locked Space and pass through.
        /// </summary>
        /// <param name="frozenPose">Pose in frozen space.</param>
        public void SetFrozenPose(Pose frozenPose)
        {
            SetLockedPose(manager.LockedFromFrozen.Multiply(frozenPose));
        }

        /// <summary>
        /// Transform pose to Locked Space and pass through.
        /// </summary>
        /// <param name="spongyPose">Pose in spongy space.</param>
        public void SetSpongyPose(Pose spongyPose)
        {
            SetLockedPose(manager.LockedFromSpongy.Multiply(spongyPose));
        }

        /// <summary>
        /// Record the locked pose and push data to the manager.
        /// </summary>
        /// <param name="lockedPose"></param>
        public virtual void SetLockedPose(Pose lockedPose)
        {
            this.lockedPose = lockedPose;

            IAlignmentManager mgr = manager.AlignmentManager;

            PushAlignmentData(mgr);

            SendAlignmentData(mgr);
        }

        [Obsolete("SetAdjustedPosition deprecated - use SetLockedPosition")]
        public void SetAdjustedPose(Pose lockedPose)
        {
            SetLockedPose(lockedPose);
        }

        /// <summary>
        /// Go back to initial state, including removal of self-artifacts from alignment manager.
        /// </summary>
        public virtual void Reset()
        {
            if (PinActive)
            {
                manager.AlignmentManager.RemoveAlignmentAnchor(AnchorId);
                AnchorId = AnchorId.Unknown;
                ReleaseAttachment();
                Debug.Assert(!PinActive);
            }
        }
        #endregion Public APIs

        #region Internal

        #region Alignment management

        /// <summary>
        /// Check if an attachment point is needed, if so then setup and make current.
        /// </summary>
        private void CheckAttachment()
        {
            if (!PinActive)
            {
                return;
            }
            ForceAttachment();
        }

        /// <summary>
        /// Ensure that there is an attachment, and it is positioned up to date.
        /// </summary>
        protected void ForceAttachment()
        {
            IAttachmentPointManager mgr = manager.AttachmentPointManager;
            if (attachmentPoint == null)
            {
                attachmentPoint = mgr.CreateAttachmentPoint(LockedPose.position, null, OnLocationUpdate, null);
            }
            else
            {
                mgr.TeleportAttachmentPoint(attachmentPoint, LockedPose.position, null);
            }
        }

        /// <summary>
        /// Dispose of any previously created attachment point.
        /// </summary>
        protected void ReleaseAttachment()
        {
            if (attachmentPoint != null)
            {
                manager.AttachmentPointManager.ReleaseAttachmentPoint(attachmentPoint);
                attachmentPoint = null;
            }
        }

        /// <summary>
        /// Callback for refit operations. Apply adjustment transform to locked pose.
        /// </summary>
        /// <param name="adjustment">Adjustment to apply.</param>
        protected virtual void OnLocationUpdate(Pose adjustment)
        {
            LockedPose = adjustment.Multiply(LockedPose);
        }

        /// <summary>
        /// Callback on notification of the alignment manager's database to check
        /// if this preset has been persisted, and restore it to operation if it has.
        /// </summary>
        protected virtual void RestoreOnLoad()
        {
            AnchorId = manager.AlignmentManager.RestoreAlignmentAnchor(AnchorName, ModelingPose);
            if (PinActive)
            {
                Pose restorePose;
                bool found = manager.AlignmentManager.GetAlignmentPose(AnchorId, out restorePose);
                Debug.Assert(found);
                lockedPose = restorePose;
            }
            CheckAttachment();
        }

        /// <summary>
        /// Communicate the data from this point to the alignment manager.
        /// </summary>
        /// <param name="mgr"></param>
        protected void PushAlignmentData(IAlignmentManager mgr)
        {
            if (PinActive)
            {
                mgr.RemoveAlignmentAnchor(AnchorId);
            }
            AnchorId = mgr.AddAlignmentAnchor(AnchorName, ModelingPose, lockedPose);
        }

        /// <summary>
        /// Notify the manager that all necessary updates have been submitted and
        /// are ready for processing.
        /// </summary>
        /// <param name="mgr"></param>
        protected void SendAlignmentData(IAlignmentManager mgr)
        {
            mgr.SendAlignmentAnchors();

            CheckAttachment();

            transform.SetGlobalPose(InitialPose);
        }

        #endregion Alignment management

        #endregion Internal

    }
}