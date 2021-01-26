// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

//#define WLT_ARCORE_EXTRA_DEBUGGING
#define WLT_ARCORE_MOVE_ANCHORS

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if WLT_ARCORE_SDK_INCLUDED
using GoogleARCore;
#endif // WLT_ARCORE_SDK_INCLUDED

namespace Microsoft.MixedReality.WorldLocking.Core
{
    public class SpongyAnchorARCore : SpongyAnchor
    {
        public static float TrackingStartDelayTime = 0.3f;

        private float lastNotLocatedTime = float.NegativeInfinity;

#if WLT_ARCORE_SDK_INCLUDED
        private GoogleARCore.Anchor internalAnchor = null;
#endif // WLT_ARCORE_SDK_INCLUDED

        /// <summary>
        /// Returns true if the anchor is reliably located. False might mean loss of tracking or not fully initialized.
        /// </summary>
        public override bool IsLocated =>
             IsReliablyLocated && Time.unscaledTime > lastNotLocatedTime + TrackingStartDelayTime;

        private bool IsReliablyLocated
        {
            get
            {
#if WLT_ARCORE_SDK_INCLUDED
                return internalAnchor != null && internalAnchor.TrackingState == GoogleARCore.TrackingState.Tracking;
#else // WLT_ARCORE_SDK_INCLUDED
                return false;
#endif // WLT_ARCORE_SDK_INCLUDED
            }
        }

        public override Pose SpongyPose
        {
            get
            {
                return transform.GetGlobalPose();
            }
        }

        // Update is called once per frame
        private void Update()
        {
            if (!IsReliablyLocated)
            {
                lastNotLocatedTime = Time.unscaledTime;
            }
#if WLT_ARCORE_SDK_INCLUDED
            else
            {
                Pose anchorGlobalPose = internalAnchor.transform.GetGlobalPose();
#if WLT_ARCORE_MOVE_ANCHORS
                anchorGlobalPose = MovePose(anchorGlobalPose);
#endif // WLT_ARCORE_MOVE_ANCHORS
#if WLT_ARCORE_EXTRA_DEBUGGING
                Vector3 move = anchorGlobalPose.position - transform.position;
                if (move.magnitude > 0.001f)
                {
                    Debug.Log($"{name} Moving by {move.ToString("F3")}");
                }
#endif // WLT_ARCORE_EXTRA_DEBUGGING
                transform.SetGlobalPose(anchorGlobalPose);
            }
#endif // WLT_ARCORE_SDK_INCLUDED
        }

        // Start is called before the first frame update
        private void Start()
        {
#if WLT_ARCORE_SDK_INCLUDED
            internalAnchor = Session.CreateAnchor(transform.GetGlobalPose());
#endif // WLT_ARCORE_SDK_INCLUDED
        }

#if WLT_ARCORE_MOVE_ANCHORS
        private float Period = 5.0f;

        private float Amplitude = 0.3f;

        private Pose MovePose(Pose globalPose)
        {
            float age = Time.time;

            float modAge = (float)(age / Period);
            modAge -= Mathf.Floor(modAge);

            Vector3 move = new Vector3(Mathf.Sin(modAge * Mathf.PI * 2.0f) * Amplitude, 0, 0);

            Debug.Log($"move={move.ToString("F3")}");

            globalPose.position = globalPose.position + move;

            return globalPose;
        }
#endif // WLT_ARCORE_MOVE_ANCHORS
    }
}