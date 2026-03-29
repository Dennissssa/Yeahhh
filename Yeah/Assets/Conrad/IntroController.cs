using NUnit.Framework;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class IntroController : MonoBehaviour
{
    public List<Sprite> bossSprites = new List<Sprite>();
    public List<Sprite> samSprites = new List<Sprite>();
    public List<Sprite> creatorSprites = new List<Sprite>();
    public List<Sprite> playerSprites = new List<Sprite>();
    
    public List<string> dialogue = new List<string>();

    public RawImage dialogueHead;

    public TextMeshProUGUI dialogueText;
    public TextMeshProUGUI dialogueName;

    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
