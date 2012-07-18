﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Postworthy.Models.Repository;
using LinqToTwitter;

namespace Postworthy.Models.Twitter
{
    public class Tweep : RepositoryEntity
    {
        public enum TweepType
        {
            Mutual,
            Follower,
            Following
        }

        private User _User;
        private TweepType _Type;

        public User User { get { return _User; } set { SetNotifyingProperty("User", ref _User, value); } }
        public TweepType Type { get { return _Type; } set { SetNotifyingProperty("Type", ref _Type, value); } }

        public Tweep() { }

        public Tweep(User user, TweepType type)
        {
            User = user;
            if (type == TweepType.Follower && user.Following)
                Type = TweepType.Mutual;
            else
                Type = type;
        }

        public override bool IsEqual(RepositoryEntity other)
        {
            if (other is Tweep)
            {
                var otherTweep = other as Tweep;
                return this.User.UserID == otherTweep.User.UserID;
            }
            else
                return false;
        }

        public override string UniqueKey
        {
            get
            {
                return "tweep_" + this.User.UserID;
            }
        }
    }
}