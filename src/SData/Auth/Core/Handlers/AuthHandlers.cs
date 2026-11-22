using Mediator;
using Microsoft.EntityFrameworkCore;
using SData.Auth.Services;
using SData.Database;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using SData.Infra.Services;

namespace SData.Auth.Handlers;

/// <summary>
/// 登录处理.
/// </summary>
public class LoginCommandHandler : IRequestHandler<LoginCommand, LoginCommandResponse>
{
    private readonly DatabaseContext _databaseContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IRsaProvider _rsaProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginCommandHandler"/> class.
    /// </summary>
    public LoginCommandHandler(DatabaseContext databaseContext, IPasswordHasher passwordHasher, ITokenService tokenService, IRsaProvider rsaProvider)
    {
        _databaseContext = databaseContext;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _rsaProvider = rsaProvider;
    }

    /// <inheritdoc/>
    public async ValueTask<LoginCommandResponse> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await _databaseContext.Users
            .Where(x => x.Username == request.Username)
            .FirstOrDefaultAsync(cancellationToken);

        if (user == null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            throw BusinessException.Throw(401, "登录失败，用户名或密码错误");
        }

        var (token, expireTime) = _tokenService.CreateToken(user.Id, user.Username, user.Role);
        return new LoginCommandResponse
        {
            Token = token,
            ExpireTime = expireTime,
            UserId = user.Id,
            Username = user.Username,
            Role = user.Role,
            RsaPublic = _rsaProvider.GetPublicPem(),
        };
    }
}

/// <summary>
/// 修改密码处理.
/// </summary>
public class ChangePasswordCommandHandler : IRequestHandler<ChangePasswordCommand, Infra.Models.EmptyCommandResponse>
{
    private readonly DatabaseContext _databaseContext;
    private readonly IPasswordHasher _passwordHasher;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangePasswordCommandHandler"/> class.
    /// </summary>
    public ChangePasswordCommandHandler(DatabaseContext databaseContext, IPasswordHasher passwordHasher)
    {
        _databaseContext = databaseContext;
        _passwordHasher = passwordHasher;
    }

    /// <inheritdoc/>
    public async ValueTask<Infra.Models.EmptyCommandResponse> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await _databaseContext.Users
            .Where(x => x.Id == request.ContextUserId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BusinessException.Throw(404, "用户不存在");

        if (!_passwordHasher.Verify(request.OldPassword, user.PasswordHash))
        {
            throw BusinessException.Throw(400, "旧密码错误");
        }

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        await _databaseContext.SaveChangesAsync(cancellationToken);
        return new Infra.Models.EmptyCommandResponse();
    }
}

/// <summary>
/// 查询当前用户处理.
/// </summary>
public class QueryCurrentUserCommandHandler : IRequestHandler<QueryCurrentUserCommand, CurrentUserModel>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryCurrentUserCommandHandler"/> class.
    /// </summary>
    public QueryCurrentUserCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async ValueTask<CurrentUserModel> Handle(QueryCurrentUserCommand request, CancellationToken cancellationToken)
    {
        var user = await _databaseContext.Users
            .Where(x => x.Id == request.ContextUserId)
            .Select(x => new CurrentUserModel
            {
                Id = x.Id,
                Username = x.Username,
                Role = x.Role,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BusinessException.Throw(404, "用户不存在");

        return user;
    }
}

/// <summary>
/// 创建用户处理.
/// </summary>
public class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, Infra.Models.Simple<long>>
{
    private readonly DatabaseContext _databaseContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IIdProvider _idProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CreateUserCommandHandler"/> class.
    /// </summary>
    public CreateUserCommandHandler(DatabaseContext databaseContext, IPasswordHasher passwordHasher, IIdProvider idProvider)
    {
        _databaseContext = databaseContext;
        _passwordHasher = passwordHasher;
        _idProvider = idProvider;
    }

    /// <inheritdoc/>
    public async ValueTask<Infra.Models.Simple<long>> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        if (request.Role == SDataRoles.Admin)
        {
            throw BusinessException.Throw(400, "不允许创建超级管理员账号");
        }

        var exists = await _databaseContext.Users.AnyAsync(x => x.Username == request.Username, cancellationToken);
        if (exists)
        {
            throw BusinessException.Throw(409, "用户名已存在");
        }

        var user = new Database.Entities.UserEntity
        {
            Id = _idProvider.GetLongId(),
            Username = request.Username,
            Role = request.Role,
            PasswordHash = _passwordHasher.Hash(request.Password),
        };

        _databaseContext.Users.Add(user);
        await _databaseContext.SaveChangesAsync(cancellationToken);
        return new Infra.Models.Simple<long> { Value = user.Id };
    }
}

/// <summary>
/// 删除用户处理.
/// </summary>
public class DeleteUserCommandHandler : IRequestHandler<DeleteUserCommand, Infra.Models.EmptyCommandResponse>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeleteUserCommandHandler"/> class.
    /// </summary>
    public DeleteUserCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async ValueTask<Infra.Models.EmptyCommandResponse> Handle(DeleteUserCommand request, CancellationToken cancellationToken)
    {
        if (request.UserId == request.ContextUserId)
        {
            throw BusinessException.Throw(400, "不允许删除当前登录账号");
        }

        var user = await _databaseContext.Users
            .Where(x => x.Id == request.UserId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BusinessException.Throw(404, "用户不存在");

        if (user.Role == SDataRoles.Admin)
        {
            throw BusinessException.Throw(400, "不允许删除超级管理员账号");
        }

        _databaseContext.Users.Remove(user);
        await _databaseContext.SaveChangesAsync(cancellationToken);
        return new Infra.Models.EmptyCommandResponse();
    }
}

/// <summary>
/// 重置用户密码处理.
/// </summary>
public class ResetUserPasswordCommandHandler : IRequestHandler<ResetUserPasswordCommand, Infra.Models.EmptyCommandResponse>
{
    private readonly DatabaseContext _databaseContext;
    private readonly IPasswordHasher _passwordHasher;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResetUserPasswordCommandHandler"/> class.
    /// </summary>
    public ResetUserPasswordCommandHandler(DatabaseContext databaseContext, IPasswordHasher passwordHasher)
    {
        _databaseContext = databaseContext;
        _passwordHasher = passwordHasher;
    }

    /// <inheritdoc/>
    public async ValueTask<Infra.Models.EmptyCommandResponse> Handle(ResetUserPasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await _databaseContext.Users
            .Where(x => x.Id == request.UserId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BusinessException.Throw(404, "用户不存在");

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        await _databaseContext.SaveChangesAsync(cancellationToken);
        return new Infra.Models.EmptyCommandResponse();
    }
}

/// <summary>
/// 修改用户权限处理.
/// </summary>
public class UpdateUserRoleCommandHandler : IRequestHandler<UpdateUserRoleCommand, Infra.Models.EmptyCommandResponse>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateUserRoleCommandHandler"/> class.
    /// </summary>
    public UpdateUserRoleCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async ValueTask<Infra.Models.EmptyCommandResponse> Handle(UpdateUserRoleCommand request, CancellationToken cancellationToken)
    {
        var user = await _databaseContext.Users
            .Where(x => x.Id == request.UserId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BusinessException.Throw(404, "用户不存在");

        if (user.Role == SDataRoles.Admin)
        {
            throw BusinessException.Throw(400, "不允许修改超级管理员权限");
        }

        user.Role = request.Role;
        await _databaseContext.SaveChangesAsync(cancellationToken);
        return new Infra.Models.EmptyCommandResponse();
    }
}

/// <summary>
/// 查询用户列表处理.
/// </summary>
public class QueryUsersCommandHandler : IRequestHandler<QueryUsersCommand, Infra.Models.PagedResult<UserItem>>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryUsersCommandHandler"/> class.
    /// </summary>
    public QueryUsersCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async ValueTask<Infra.Models.PagedResult<UserItem>> Handle(QueryUsersCommand request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var query = _databaseContext.Users.AsNoTracking().OrderBy(x => x.Id);
        var count = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new UserItem
            {
                Id = x.Id,
                Username = x.Username,
                Role = x.Role,
                CreateTime = x.CreateTime,
            })
            .ToListAsync(cancellationToken);

        return new Infra.Models.PagedResult<UserItem>
        {
            Page = page,
            PageSize = pageSize,
            Count = count,
            Data = items,
        };
    }
}
