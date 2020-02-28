using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Resemble
{
    public class Clip : ScriptableObject
    {
        public Speech speech;
        public AudioClip clip;
        public AudioClip clipCopy;
        public Text text;
        public string uuid;
    }
}