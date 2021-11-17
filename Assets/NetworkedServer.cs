using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

public class NetworkedServer : MonoBehaviour
{
    int maxConnections = 1000;
    int reliableChannelID;
    int unreliableChannelID;
    int hostID;
    int socketPort = 5491;
    LinkedList<PlayerAccount> playerAccounts;
    LinkedList<GameSession> gameSessions;

    private string playerAccountFilePath;
    private int playerWaitingForMatch  = -1;
    private int markLocation = -1;
    private int stepIndex = 0;
    
    // Start is called before the first frame update
    void Start()
    {
        NetworkTransport.Init();
        ConnectionConfig config = new ConnectionConfig();
        reliableChannelID = config.AddChannel(QosType.Reliable);
        unreliableChannelID = config.AddChannel(QosType.Unreliable);
        HostTopology topology = new HostTopology(config, maxConnections);
        hostID = NetworkTransport.AddHost(topology, socketPort, null);
        playerAccounts = new LinkedList<PlayerAccount>();
        gameSessions = new LinkedList<GameSession>();
        playerAccountFilePath = Application.dataPath + Path.DirectorySeparatorChar + "PlayerAccountData.txt";
        
        LoadPlayerAccounts();
    }

    // Update is called once per frame
    void Update()
    {
        int recHostID;
        int recConnectionID;
        int recChannelID;
        byte[] recBuffer = new byte[1024];
        int bufferSize = 1024;
        int dataSize;
        byte error = 0;

        NetworkEventType recNetworkEvent = NetworkTransport.Receive(out recHostID, out recConnectionID, out recChannelID, recBuffer, bufferSize, out dataSize, out error);

        switch (recNetworkEvent)
        {
            case NetworkEventType.Nothing:
                break;
            case NetworkEventType.ConnectEvent:
                Debug.Log("Connection, " + recConnectionID);
                break;
            case NetworkEventType.DataEvent:
                string msg = Encoding.Unicode.GetString(recBuffer, 0, dataSize);
                ProcessRecievedMsg(msg, recConnectionID);
                break;
            case NetworkEventType.DisconnectEvent:
                Debug.Log("Disconnection, " + recConnectionID);
                break;
        }
    }
  
    public void SendMessageToClient(string msg, int id)
    {
        byte error = 0;
        byte[] buffer = Encoding.Unicode.GetBytes(msg);
        NetworkTransport.Send(hostID, id, reliableChannelID, buffer, msg.Length * sizeof(char), out error);
    }
    
    private void ProcessRecievedMsg(string msg, int id)
    {
        Debug.Log("msg recieved = " + msg + ".  connection id = " + id);

        string[] csv = msg.Split(',');

        int signifier = int.Parse(csv[0]);

        if (signifier == ClientToServerSignifiers.CreateAccount)
        {
            string n = csv[1];
            string p = csv[2];

            bool isUnique = true;
            
            foreach (var pa in playerAccounts)
            {
                if (pa.name == n)
                {
                   isUnique = false;
                    break;
                }
            }
            if (isUnique)
            {
                playerAccounts.AddLast(new PlayerAccount(n, p));
                SendMessageToClient(ServerToClientSiginifiers.LoginResponse + "," + LoginResponses.Success, id);
                
                //save player account list
                SavePlayerAccounts();
            }
            else
            {
                SendMessageToClient(ServerToClientSiginifiers.LoginResponse + "," + LoginResponses.FailureNameInUse, id);
            }
        }
        else if (signifier == ClientToServerSignifiers.Login)
        {
            string n = csv[1];
            string p = csv[2];

            bool hasBeenFound = false;

            foreach (var pa in playerAccounts)
            {
                if (pa.name == n)
                {
                    if (pa.password == p)
                    {
                        SendMessageToClient(ServerToClientSiginifiers.LoginResponse + "," + LoginResponses.Success, id);
                    }
                    else
                    {
                        SendMessageToClient(ServerToClientSiginifiers.LoginResponse + "," + LoginResponses.FailureIncorrectPassword, id);
                    }
                    
                    hasBeenFound = true;
                    break;
                }
            }
            
            if (!hasBeenFound)
            {
                SendMessageToClient(ServerToClientSiginifiers.LoginResponse + "," + LoginResponses.FailureNameNotFound, id);
            }
        }
        else if (signifier == ClientToServerSignifiers.AddToGameSessionQueue)
        {
            //if there is no player waiting, save the waiting player in the above variable
            if (playerWaitingForMatch == -1)
            {
                //make a single int variable to represent the one and only possible waiting player
                playerWaitingForMatch = id;   
            }
            else //if there is one waiting player, join the session
            {
                //Create the game session object, pass it to two players
                GameSession gs = new GameSession(playerWaitingForMatch, id);
                gameSessions.AddLast(gs);
                UpdateGSIndex();
                //Pass a signifier to both clients that they've joined one
                SendMessageToClient(ServerToClientSiginifiers.GameSessionStarted + "", id);
                SendMessageToClient(ServerToClientSiginifiers.GameSessionStarted + "", playerWaitingForMatch);
               
                playerWaitingForMatch = -1;
            }
        }
        else if (signifier == ClientToServerSignifiers.TicTacToePlay)
        {
            Debug.Log("Our next action item beckons");
            int firstPlayerMark = Random.Range(0, 1);
            int secondPlayerMark;
            if (firstPlayerMark == 0)
            {
                secondPlayerMark = 1;
            }
            else
            {
                secondPlayerMark = 0;
            }

            GameSession gs = FindGameSessionWithPlayerID(id);
            if (gs.playerID1 == id)
                SendMessageToClient(ServerToClientSiginifiers.OpponentTicTacToePlay + "," + firstPlayerMark,
                    gs.playerID1);
            else
                SendMessageToClient(ServerToClientSiginifiers.OpponentTicTacToePlay + "," + secondPlayerMark,
                    gs.playerID2);
        }
        else if (signifier == ClientToServerSignifiers.DrawMark)
        {
            Debug.Log("Draw Mark");
            markLocation = int.Parse(csv[1]);
            
            GameSession gs = FindGameSessionWithPlayerID(id);
            if (gs.playerID1 == id)
                SendMessageToClient(ServerToClientSiginifiers.DrawMark + "," + markLocation,
                    gs.playerID2);
            else
                SendMessageToClient(ServerToClientSiginifiers.DrawMark + "," + markLocation,
                    gs.playerID1);

            if (gs.observerList.Count > 0)
            {
                foreach (int observer in gs.observerList)
                {
                    SendMessageToClient(
                        ServerToClientSiginifiers.DrawMarkOnObserver + "," + markLocation + "," + int.Parse(csv[2]),
                        observer);
                }
            }

            gs.AddStep(int.Parse(csv[2]), markLocation);
            
            markLocation = -1;
        }
        else if (signifier == ClientToServerSignifiers.GameOver)
         {
             GameSession gs = FindGameSessionWithPlayerID(id);
             if (gs == null)
             {
                 return;
             }
             SendMessageToClient(ServerToClientSiginifiers.GameOver + "," + 0, gs.playerID1);
             SendMessageToClient(ServerToClientSiginifiers.GameOver + "," + 0, gs.playerID2);
             foreach (Step step in gs.steps)
             {
                 if (step.location == -1 || step.mark == -1)
                 {
                     break;
                 }
                 else
                 {
                     //send every step of TicTacToe
                     SendMessageToClient(
                         ServerToClientSiginifiers.GameOver + "," + 1 + "," + step.location + "," + step.mark,
                         gs.playerID1);
                     SendMessageToClient(
                         ServerToClientSiginifiers.GameOver + "," + 1 + "," + step.location + "," + step.mark,
                         gs.playerID2);
                 }
             }
             gameSessions.Remove(FindGameSessionWithPlayerID(id));
             UpdateGSIndex();
        }
         else if (signifier == ClientToServerSignifiers.AskForGSList)
         {
             int GSNmuber = gameSessions.Count;
             SendMessageToClient(ServerToClientSiginifiers.GSList + "," + 0 + "," + GSNmuber, id);
             foreach (GameSession gs in gameSessions)
             {
                 SendMessageToClient(
                     ServerToClientSiginifiers.GSList + "," + 1 + "," + gs.playerID1 + "," + gs.playerID2, id);
             }
             SendMessageToClient(ServerToClientSiginifiers.GSList + "," + 2, id);
         }
         else if (signifier == ClientToServerSignifiers.AskJoinGS)
         {
             int id1 = int.Parse(csv[1]);
             int id2 = int.Parse(csv[2]);
             GameSession gs = FindGameSessionWithPlayerID(id1);
             if ((gs.playerID1 == id1 && gs.playerID2 == id2) || (gs.playerID1 == id2 && gs.playerID2 == id1))
             {
                 //Romm exists, send join succeed message
                 SendMessageToClient(ServerToClientSiginifiers.JoiningRoom + "," + 0, id);
                 foreach (Step step in gs.steps)
                 {
                     if (step.location == -1 || step.mark == -1)
                     {
                         break;
                     }
                     else
                     {
                         //send every step of TicTacToe
                         SendMessageToClient(
                             ServerToClientSiginifiers.JoiningRoom + "," + 1 + "," + step.location + "c" + step.mark,
                             id);
                     }
                 }
             }
             else
             {
                 //Room doesn't exist now, send join failed message
                 SendMessageToClient(ServerToClientSiginifiers.JoiningRoom + "," + 2, id);
             }
         }
         else if (signifier == ClientToServerSignifiers.JoinRandomRoom)
         {
             int gsCount = gameSessions.Count;
             if (gsCount <= 0)
             {
                 SendMessageToClient(ServerToClientSiginifiers.NoRoomCanJoin + "", id);
                 return;
             }

             List<int> tempList=new List<int>();

             foreach (GameSession gs in gameSessions)
             {
                 tempList.Add(gs.playerID1);
                 tempList.Add(gs.playerID2);
                 //Debug.Log("add id " + gs.playerID1 + " and id " + gs.playerID2);
             }

             int randomIndex = Random.Range(0, tempList.Count - 1);
             //Debug.Log("temp list count " + tempList.Count);
             //Debug.Log("random id " + tempList[randomIndex]);
             GameSession tempGS = FindGameSessionWithPlayerID(tempList[randomIndex]);
             tempGS.observerList.Add(id);
             
             SendMessageToClient(ServerToClientSiginifiers.JoiningRoom + "," + 0, id);
             foreach (Step step in tempGS.steps)
             {
                 if (step.location == -1 || step.mark == -1)
                 {
                     break;
                 }
                 else
                 {
                     //send every step of TicTacToe
                     SendMessageToClient(
                         ServerToClientSiginifiers.JoiningRoom + "," + 1 + "," + step.location + "," + step.mark,
                         id);
                     Debug.Log("send step" + step.location + " " + step.mark);
                 }
             }
             SendMessageToClient(ServerToClientSiginifiers.JoiningRoom + "," + 2, id);
         }
        else if (signifier == ClientToServerSignifiers.SendChatMessage)
        {
            GameSession gs = FindGameSessionWithPlayerID(id);
            if (gs.playerID1 == id)
            {
                SendMessageToClient(ServerToClientSiginifiers.SendChatMessage + "," + csv[1], gs.playerID2);
            }
            else
            {
                SendMessageToClient(ServerToClientSiginifiers.SendChatMessage + "," + csv[1], gs.playerID1);
            }
        }
    }

    private void SavePlayerAccounts()
    {
        StreamWriter sw =
            new StreamWriter(playerAccountFilePath);
        
        foreach (var pa in playerAccounts)
        {
            sw.WriteLine(pa.name + "," + pa.password);
        }
        
        sw.Close();
    }

    private void LoadPlayerAccounts()
    {
        if (File.Exists(playerAccountFilePath))
        {
            StreamReader sr =
                new StreamReader(playerAccountFilePath);

            string line;
            while ((line = sr.ReadLine()) != null)
            {
                string[] csv = line.Split(',');

                PlayerAccount pa = new PlayerAccount(csv[0], csv[1]);
                playerAccounts.AddLast(pa);
            }
        }
    }

    private GameSession FindGameSessionWithPlayerID(int id)
    {
        foreach (var gs in gameSessions)
        {
            if (gs.playerID1 == id || gs.playerID2 == id)
                return gs;
        }
        
        Debug.Log("Cannot find game session");
        return null;
    }

    private void UpdateGSIndex()
    {
        int i = 0;
        foreach (GameSession gs in gameSessions)
        {
            gs.index = i;
            i++;
        }
    }

    public class PlayerAccount
    {
        public string name,password;
        
        public PlayerAccount(string name, string password)
        {
            this.name = name;
            this.password = password;
        }
    }

    public class GameSession
    {
        public int playerID1, playerID2; //add getter & setter later
        //public Step[] steps = new Step[9];
        public List<Step> steps;
        public List<int> observerList;
        public int index;
        

        public GameSession(int playerID1, int playerID2)
        {
            this.playerID1 = playerID1;
            this.playerID2 = playerID2;
            steps = new List<Step>();
            observerList = new List<int>();
        }
        
        public void AddStep(int mark, int location)
        {
            steps.Add(new Step(mark, location));
            Debug.Log(mark + " on " + location + " capicity " + steps.Count);
        }
    }

    public class Step
    {
        public int mark;//0:O 1:X
        public int location;

        public Step()
        {
            mark = -1;
            location = -1;
        }

        public Step(int mark, int location)
        {
            this.mark = mark;
            this.location = location;
        }
    }

    public static class ClientToServerSignifiers
    {
        public const int Login = 1;
        public const int CreateAccount = 2;
        public const int AddToGameSessionQueue = 3;
        public const int TicTacToePlay = 4;
        public const int DrawMark = 5;
        public const int GameOver = 6;
        public const int AskForGSList = 7;
        public const int AskJoinGS = 8;
        public const int JoinRandomRoom = 9;
        public const int SendChatMessage = 10;
    }

    public static class ServerToClientSiginifiers
    {
        public const int LoginResponse = 1;
        public const int GameSessionStarted = 2;
        public const int OpponentTicTacToePlay = 3;
        public const int DrawMark = 4;
        public const int GSList = 5;
        public const int JoiningRoom = 6;
        public const int NoRoomCanJoin = 7;
        public const int DrawMarkOnObserver = 8;
        public const int GameOver = 9;
        public const int SendChatMessage = 10;
    }

    public static class LoginResponses
    {
        public const int Success = 1;
        public const int FailureNameInUse = 2;
        public const int FailureNameNotFound = 3;
        public const int FailureIncorrectPassword = 4; 
    }
}
