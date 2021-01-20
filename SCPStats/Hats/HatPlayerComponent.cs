﻿using System;
using System.Collections.Generic;
using Exiled.API.Features;
using MEC;
using UnityEngine;

namespace SCPStats.Hats
{
    public class HatPlayerComponent : MonoBehaviour
    {
        internal HatItemComponent item;

        private void Start()
        {
            Timing.RunCoroutine(MoveHat().CancelWith(this).CancelWith(gameObject));
        }

        private IEnumerator<float> MoveHat()
        {

            while (true)
            {
                yield return Timing.WaitForSeconds(SCPStats.Singleton.Config.HatUpdateTime);

                try
                {
                    if (item == null || item.gameObject == null) continue;

                    var pickup = item.gameObject.GetComponent<Pickup>();

                    var player = Player.Get(gameObject);
                    
                    var camera = player.CameraTransform;

                    var rotAngles = camera.rotation.eulerAngles;
                    if (player.Team == Team.SCP) rotAngles.x = 0;
                    
                    var rotation = Quaternion.Euler(rotAngles);
                    
                    var rot = rotation * item.rot;
                    var transform1 = pickup.transform;
                    var pos = (player.Role != RoleType.Scp079 ? rotation * item.pos : item.pos) + camera.position;

                    pickup.Networkposition = pos;
                    transform1.position = pos;
                    
                    transform1.rotation = rot;
                    pickup.Networkrotation = rot;
                    
                    pickup.UpdatePosition();
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
        }
    }
}