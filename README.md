# 開発中のライブラリです 大きな変更が発生する可能性があります

# 概要
RadomeはUnity Transport Packageを利用した汎用ネットワークおよびリプレイライブラリです。

https://github.com/Unity-Technologies/multiplayer

+ 上記ライブラリで未完成のNotification Layer (Reliabilityの実現)
+ Server-Client, P2P, Relay Server利用を想定したNetworkBaseクラス
+ 旧UnetのHLAPIの主要機能 (NetIDによるGameObjectの同期, Transformの同期, RPCなど)
+ Spawn / Despawnの管理 (制作予定)
+ 通信パケットを利用したリプレイ機能 (制作予定)

を提供します。

# 簡易マニュアル

## Package Managerによるインストール
Packageフォルダにあるmanifest.jsonのdependenciesにあるパッケージに以下のように記載してください。
(jp.ickx.commonはProjectICKXのUnityライブラリで汎用的に利用する機能をまとめたパッケージです。)

```
{
  "dependencies": {
    ...
    "com.unity.mathematics": "0.0.12-preview.19",
    "jp.ickx.common": "https://github.com/ProjectICKX/UnityCommon.git",
    "jp.ickx.radome": "https://github.com/ProjectICKX/Radome.git",
    ...
  }
}
```
## Server-Client接続
Serverとして起動する場合
```
ServerNetworkManager serverNetwork = new ServerNetworkManager ();
serverNetwork.Start ([任意のポート番号]);
networkBase = serverNetwork;
```

Clientとして起動する場合
```
ClientNetworkManager clientNetwork = new ClientNetworkManager ();
clientNetwork.Start ([サーバーのアドレス], [サーバーのポート番号]);
networkBase = clientNetwork;
```

上記で生成したNetworkManagerBaseのインスタンスをGamePacketManagerに登録すれば初期化は完了です
```
GamePacketManager.SetNetworkManager (networkBase);
```

## P2P接続
制作中

## パケットの送受信
GamePacketManagerを利用することでデータを送ったり受け取ることができます。  
**Send/BroadCast**メソッドでデータの送信を、**OnRecievePacket**イベントでデータの受け取りができます。  
```
GamePacketManager.SetNetworkManager (networkBase);
GamePacketManager.OnRecievePacket += OnRecievePacket; //イベントに登録する

void SendHogePacket () {
    //byte + intのデータなので5byteのWriterを作成して値を書き込む
    using (var writer = new DataStreamWriter (5, Allocator.Temp)) {
        writer.Write ((byte)UserPacketType.Hoge);
        writer.Write (Random.Range(0,100));
        writer.Write (Time.time);
        //Unreliableなら到着・順番は保証されないが低負荷
        //Reliableなら到着と順番を保証する 順番解決や再送のコストがかかる
        GamePacketManager.Brodcast (writer, QosType.Unreliable);
    }
}

void SendFugaPacket () {
    using (var writer = new DataStreamWriter (5, Allocator.Temp)) {
        writer.Write ((byte)UserPacketType.Fuga);
           ...
        //第3引数をtrueにすると、他のパケットとまとめることなく送る
        //不安定な回線でもデータはロスしにくいが、パケットの送信回数は多くなる
        GamePacketManager.Send (serverId, writer, QosType.Reliable, true);
    }
}

//パケットを受け取るコールバック
private void OnRecievePacket (ushort senderPlayerId, byte type, DataStreamReader stream, DataStreamReader.Context ctx) {
    //streamは
    //typeは200番以降はシステムで利用しているので0~199番を利用
    switch (type) {
        case (byte)UserPacketType.Hoge:
            var random = stream.ReadInt(ref ctx);
            var time = stream.ReadFloat(ref ctx);
                ...
            break;
        case (byte)PacketType.Fuga:
                ...
            break;
    }
}
```

送り先の識別にはPlayerID(ushort)を使っています。  
Host役が接続してきた順にPlayerIDを発行しており、GamePacketManager.PlayerIdで自身のPlayerIDを取得できます。

また、内部の処理はJobSystemを利用してマルチスレッド化していますが、Send/BroadCast/OnRecievePacketといったGamePacketManagerの機能はMainThreadのみでしか利用できません。JobThreadからSend/Broadcastを利用することはできません。

## RecordableIdentityの利用
RecordableIdentityを利用することで、ローカルとリモートの環境でGameObjectを同期することができます。  

RecordableIdentityは
+ SceneHash
+ NetID
の２つの値を使い識別しています。SceneHashで所属するシーンを、NetIDでシーン内のどのGameObjectかを判断します。  

この値はRecordableSceneIdentityによって管理されています。  
RecordableIdentityを持つGameObjectを利用するScene内に１つRecordableSceneIdentityを用意してください。  

**Unityの上部のメニューから "ICKX/Network/AssignNetIDAll" を実行**することで、NetIDを重複しないように割り当てます。  
(Sceneに配置しないで実行時にRecordableIdentityを生成する場合の手順は別の項で説明します。)  

**RecordableIdentityManager.GetIdentity(int sceneHash, uint netId)** メソッドを利用することでRecordableIdentityを取得することができます。

### 所有権の変更
RecordableIdentityにはauthor変数にどのPlayerIDのクライアントが所有権を持っているかを保持しています。  
authorは↓のRecordableTransformで利用したり、キャラクターの判定などの処理をどのクライアントで行うかの判断に使用してください。

Sceneに配置した場合はauthorは**サーバー(or Host)役のPlayerID=0に設定されています。**  
変更する場合は**RecordableIdentityManager.RequestChangeAuthor**メソッドを実行して変更することができます。  
authorが変更された場合はRecordableIdentity.OnChangeAuthorイベントで検知できます。

### RecordableTransform
RecordableTransformを利用することでTransformを同期することができます。  
同期したいTransformに対して、RecordableIdentityと共にアタッチしてください。  
  
Mode.Predictionでは速度を持っている場合に遅延を考慮した未来位置に移動させることで、速度があってもリモート側と近い位置に移動させることができます。
  
Transform情報の反映はRecordableIdentityを利用して、所有権を持っているクライアントからそうでないクライアントへ行われます。
移動処理を実行するクライアントに所有権を持たせる必要があります。

### RPC通信
RecordableBehaviourを継承することで、コンポーネント同士の通信を実現することができます。  
  
RecordableBehaviour.SendRpcおよびBrodcastRpcメソッドでデータを送信すれば、
リモート側ではNetIDなどの判定処理を書かなくても、OnRecieveRpcPacketメソッドでそのデータを受け取ることができます。  
  
以下のように書くことで、RpcHogeメソッドを呼ぶだけですべてクライアントでLocalHogeメソッドを実行することができます。
```
public void RpcHoge (float time, float rand) {

    using (var packet = new DataStreamWriter (8, Allocator.Temp)) {
        packet.Write (time);
        packet.Write (rand);
        BrodcastRpc ((byte)MethodId.Hoge, packet, QosType.Reliable);
    }

    LocalHoge (time, rand);
}

public void LocalHoge (float time, float rand) {
    Debug.Log ("LocalHoge : time=" + time + ", rand=" + rand);
}

public override void OnRecieveRpcPacket (ushort senderPlayerId, byte methodId, DataStreamReader rpcPacket, DataStreamReader.Context ctx) {
    switch ((MethodId)methodId) {
        case MethodId.Hoge:
            var time = rpcPacket.ReadFloat (ref ctx);
            var rand = rpcPacket.ReadFloat (ref ctx);
            LocalHoge (time, rand);
            break;
    }
}
```

### NetIDの発行とIdentityの作成

整備中

