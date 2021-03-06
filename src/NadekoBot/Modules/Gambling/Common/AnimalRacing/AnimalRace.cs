﻿using NadekoBot.Common;
using NadekoBot.Extensions;
using NadekoBot.Modules.Gambling.Common.AnimalRacing.Exceptions;
using NadekoBot.Services;
using NadekoBot.Services.Database.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NadekoBot.Modules.Gambling.Common.AnimalRacing
{
    public class AnimalRace : IDisposable
    {
        public enum Phase
        {
            WaitingForPlayers,
            Running,
            Ended,
        }

        private const int _startingDelayMiliseconds = 20_000;

        public Phase CurrentPhase = Phase.WaitingForPlayers;

        public event Func<AnimalRace, Task> OnStarted = delegate { return Task.CompletedTask; };
        public event Func<AnimalRace, Task> OnStartingFailed = delegate { return Task.CompletedTask; };
        public event Func<AnimalRace, Task> OnStateUpdate = delegate { return Task.CompletedTask; };
        public event Func<AnimalRace, Task> OnEnded = delegate { return Task.CompletedTask; };

        public ImmutableArray<AnimalRacingUser> Users => _users.ToImmutableArray();
        public List<AnimalRacingUser> FinishedUsers { get; } = new List<AnimalRacingUser>();

        private readonly SemaphoreSlim _locker = new SemaphoreSlim(1, 1);
        private readonly HashSet<AnimalRacingUser> _users = new HashSet<AnimalRacingUser>();
        private readonly CurrencyService _currency;
        private readonly Queue<RaceAnimal> _animalsQueue;
        public int MaxUsers { get; }

        public AnimalRace(CurrencyService currency, RaceAnimal[] availableAnimals)
        {
            this._currency = currency;
            this._animalsQueue = new Queue<RaceAnimal>(availableAnimals);
            this.MaxUsers = availableAnimals.Length;

            if (this._animalsQueue.Count == 0)
                CurrentPhase = Phase.Ended;
        }

        public void Initialize() //lame name
        {
            var _t = Task.Run(async () =>
            {
                await Task.Delay(_startingDelayMiliseconds).ConfigureAwait(false);

                await _locker.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (CurrentPhase != Phase.WaitingForPlayers)
                        return;

                    await Start().ConfigureAwait(false);
                }
                finally { _locker.Release(); }
            });
        }

        public async Task<AnimalRacingUser> JoinRace(ulong userId, string userName, int bet = 0)
        {
            if (bet < 0)
                throw new ArgumentOutOfRangeException(nameof(bet));

            var user = new AnimalRacingUser(userName, userId, bet);

            await _locker.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_users.Count == MaxUsers)
                    throw new AnimalRaceFullException();

                if (CurrentPhase != Phase.WaitingForPlayers)
                    throw new AlreadyStartedException();

                if (!await _currency.RemoveAsync(userId, "BetRace", bet).ConfigureAwait(false))
                    throw new NotEnoughFundsException();

                if (_users.Contains(user))
                    throw new AlreadyJoinedException();

                var animal = _animalsQueue.Dequeue();
                user.Animal = animal;
                _users.Add(user);
                
                if (_animalsQueue.Count == 0) //start if no more spots left
                    await Start().ConfigureAwait(false);

                return user;
            }
            finally { _locker.Release(); }
        }

        private async Task Start()
        {
            CurrentPhase = Phase.Running;
            if (_users.Count <= 1)
            {
                foreach (var user in _users)
                {
                    if(user.Bet > 0)
                        await _currency.AddAsync(user.UserId, "Race refund", user.Bet).ConfigureAwait(false);
                }
                
                var _sf = OnStartingFailed?.Invoke(this);
                CurrentPhase = Phase.Ended;
                return;
            }

            var _ = OnStarted?.Invoke(this);
            var _t = Task.Run(async () =>
            {
                var rng = new NadekoRandom();
                while (!_users.All(x => x.Progress >= 60))
                {
                    foreach (var user in _users)
                    {
                        user.Progress += rng.Next(1, 11);
                        if (user.Progress >= 60)
                            user.Progress = 60;
                    }

                    var finished = _users.Where(x => x.Progress >= 60 && !FinishedUsers.Contains(x))
                        .Shuffle();

                    FinishedUsers.AddRange(finished);

                    var _ignore = OnStateUpdate?.Invoke(this);
                    await Task.Delay(2500).ConfigureAwait(false);
                }

                if (FinishedUsers[0].Bet > 0)
                    await _currency.AddAsync(FinishedUsers[0].UserId, "Won a Race", FinishedUsers[0].Bet * (_users.Count - 1))
                        .ConfigureAwait(false);

                var _ended = OnEnded?.Invoke(this);
            });
        }

        public void Dispose()
        {
            CurrentPhase = Phase.Ended;
            OnStarted = null;
            OnEnded = null;
            OnStartingFailed = null;
            OnStateUpdate = null;
            _locker.Dispose();
            _users.Clear();
        }
    }
}