using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseOrleans(static siloBuilder =>
{
    siloBuilder.UseLocalhostClustering();
    siloBuilder.AddMemoryGrainStorage("urls");
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

using var app = builder.Build();

app.UseForwardedHeaders();

app.MapGet("/assignId", () =>
{
    var playerId = Guid.NewGuid().ToString();
    return Results.Ok(playerId);
});

app.MapGet("/join", async (string playerId, IGrainFactory grains) =>
{
    var queueGrain = grains.GetGrain<IQueueGrain>(0);
    await queueGrain.JoinQueue(playerId);
    return Results.Ok();
});

app.MapGet("/submit", async (string playerId, int number, IGrainFactory grains) =>
{
    var playerGrain = grains.GetGrain<IPlayerGrain>(playerId);
    var gameRoomGrain = await playerGrain.GetGameRoom();
    var gameRoom = grains.GetGrain<IGameRoomGrain>(gameRoomGrain);
    bool canSubmit = await playerGrain.SetNumber(number);
   
    gameRoom.CheckReady();
    return Results.Ok(canSubmit);
});

app.MapGet("/exit", async (string playerId, IGrainFactory grains) =>
{
    var queueGrain = grains.GetGrain<IQueueGrain>(0);
    await queueGrain.RemovePlayer(playerId);
    return Results.Ok();
});

app.MapGet("/notification", async (string playerId, IGrainFactory grains) =>
{
    var playerGrain = grains.GetGrain<IPlayerGrain>(playerId);
    var notification = await playerGrain.GetNotification();
    return Results.Ok(notification);
});

app.Run();

public interface IPlayerGrain : IGrainWithStringKey
{
    Task<bool> SetNumber(int number);
    Task<int> GetNumber();
    Task<int> GetScore();
    Task IncrementScore();
    Task SetGameRoom(Guid gameRoomId);
    Task<Guid> GetGameRoom();
    Task SetId(string playerId);
    Task SendMessage(string? youLoose);
    
    Task<string?> GetNotification();
}

public class PlayerGrain : Grain, IPlayerGrain
{
    private int _number;
    private int _score;
    private Guid _gameRoomId;
    private string _id;
    private readonly ILogger<PlayerGrain> _logger;
    private string? _notificationMessage;
    
    public PlayerGrain(ILogger<PlayerGrain> logger)
    {
        _logger = logger;
    }
    
    public Task<bool> SetNumber(int number)
    {
        Task<bool> isOk = GrainFactory.GetGrain<IGameRoomGrain>(_gameRoomId).TryToSetNumber(_id, number);
        if (isOk.Result)
        {
            return Task.FromResult(false);
        }
        _number = number;
        _logger.LogInformation("Player {PlayerId} set number to {Number}", this.GetPrimaryKeyString(), number);
        return Task.FromResult(true);
    }

    public Task<int> GetNumber() => Task.FromResult(_number);

    public Task<int> GetScore() => Task.FromResult(_score);

    public Task IncrementScore()
    {
        _score++;
        _logger.LogInformation("Player {PlayerId} score incremented to {Score}", this.GetPrimaryKeyString(), _score);
        return Task.CompletedTask;
    }

    public Task SetGameRoom(Guid gameRoomId)
    {
        _gameRoomId = gameRoomId;
        _logger.LogInformation("Player {PlayerId} assigned to game room {GameRoomId}", this.GetPrimaryKeyString(), gameRoomId);
        return Task.CompletedTask;
    }

    public Task<Guid> GetGameRoom() => Task.FromResult(_gameRoomId);
    public Task SetId(string playerId)
    {
        _id = playerId;
        return Task.CompletedTask;
    }

    public Task SendMessage(string? textMessage)
    {
        _notificationMessage = textMessage;
        return Task.CompletedTask;
    }
    
    public Task<string?> GetNotification()
    {
        var message = _notificationMessage;
        _notificationMessage = null; // очищаем сообщение после получения
        return Task.FromResult(message);
    }
}

public interface IGameRoomGrain : IGrainWithGuidKey
{
    Task AddPlayer(string playerId, Guid gameRoomId);
    void CheckReady();
    Task<bool> TryToSetNumber(string id, int number);
}

public class GameRoomGrain : Grain, IGameRoomGrain
{
    private List<string> _players = new List<string>();
    private Dictionary<string, int?> _playerNumbers = new Dictionary<string, int?>();
    private int _targetNumber;
    private readonly ILogger<GameRoomGrain> _logger;

    public GameRoomGrain(ILogger<GameRoomGrain> logger)
    {
        _logger = logger;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _targetNumber = new Random().Next(0, 101);
        _logger.LogInformation("Game room {GameRoomId} created with target number {TargetNumber}", this.GetPrimaryKey(), _targetNumber);
        return base.OnActivateAsync(cancellationToken);
    }

    public Task AddPlayer(string playerId, Guid gameRoomId)
    {
        IPlayerGrain playerGrain = GrainFactory.GetGrain<IPlayerGrain>(playerId);
        playerGrain.SetId(playerId);
        playerGrain.SetGameRoom(gameRoomId);
        _players.Add(playerId);
        _playerNumbers[playerId] = null;
        _logger.LogInformation("Player {PlayerId} added to game room {GameRoomId}", playerId, this.GetPrimaryKey());
        return Task.CompletedTask;
    }

    public void CheckReady()
    {
        if (_players.Count != 2)
            return;

        var player1 = _players[0];
        var player2 = _players[1];

        if (_playerNumbers[player1] == null || _playerNumbers[player2] == null)
            return;

        var player1Number = _playerNumbers[player1].Value;
        var player2Number = _playerNumbers[player2].Value;

        _logger.LogInformation("Player {Player1Id} guessed {Player1Number}, Player {Player2Id} guessed {Player2Number} in game room {GameRoomId}",
            player1, player1Number, player2, player2Number, this.GetPrimaryKey());

        var player1Difference = Math.Abs(player1Number - _targetNumber);
        var player2Difference = Math.Abs(player2Number - _targetNumber);

        if (player1Difference == player2Difference)
        {
            _logger.LogInformation("It's a tie in game room {GameRoomId}", this.GetPrimaryKey());
            return;
        }

        var winner = player1Difference < player2Difference ? player1 : player2;
        var loser = winner == player1 ? player2 : player1;

        GrainFactory.GetGrain<IPlayerGrain>(winner).IncrementScore();
        GrainFactory.GetGrain<IPlayerGrain>(winner).SendMessage($"You win! The number was {_targetNumber}");
        GrainFactory.GetGrain<IPlayerGrain>(loser).SendMessage($"You lose! The number was {_targetNumber}");
        
        _playerNumbers[player1] = null;
        _playerNumbers[player2] = null;

        _targetNumber = new Random().Next(0, 101);
        
        _logger.LogInformation("Player {WinnerId} wins in game room {GameRoomId}", winner, this.GetPrimaryKey());
        _logger.LogInformation("New number is {TargetNumber} in game room {GameRoomId}", _targetNumber, this.GetPrimaryKey());
    }


    public Task<bool> TryToSetNumber(string playerId, int number)
    {
        if (_players.Count <= 1)
        {
            return Task.FromResult(false);
        }
        _playerNumbers[playerId] = number;
        _logger.LogInformation("Player {PlayerId} set number to {Number} in game room {GameRoomId}", playerId, number, this.GetPrimaryKey());
        return Task.FromResult(true);
    }
}

public interface IQueueGrain : IGrainWithIntegerKey
{
    Task JoinQueue(string playerId);
    Task RemovePlayer(string playerId);
}

public class QueueGrain : Grain, IQueueGrain
{
    private Queue<string> _queue = new Queue<string>();
    private readonly ILogger<QueueGrain> _logger;

    public QueueGrain(ILogger<QueueGrain> logger)
    {
        _logger = logger;
    }

    public async Task JoinQueue(string playerId)
    {
        _queue.Enqueue(playerId);
        _logger.LogInformation("Player {PlayerId} joined the queue", playerId);

        if (_queue.Count >= 2)
        {
            var playerID1 = _queue.Dequeue();
            var playerID2 = _queue.Dequeue();

            var gameRoomId = Guid.NewGuid();
            var gameRoom = GrainFactory.GetGrain<IGameRoomGrain>(gameRoomId);
            await gameRoom.AddPlayer(playerID1,gameRoomId);
            await gameRoom.AddPlayer(playerID2,gameRoomId);

            _logger.LogInformation("Game room {GameRoomId} started with players {Player1Id} and {Player2Id}", gameRoomId, playerID1, playerID2);
        }
    }

    public Task RemovePlayer(string playerId)
    {
        var newQueue = new Queue<string>(_queue.Where(p => p != playerId));
        _queue = newQueue;
        _logger.LogInformation("Player {PlayerId} left the queue", playerId);
        return Task.CompletedTask;
    }
}
